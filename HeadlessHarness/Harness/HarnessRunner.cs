using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HeadlessHarness.Core;
using KSA;
using KSA.Kfs;

namespace HeadlessHarness.Harness;

// Loads consumer mod assemblies and runs the IHarnessTests they (and the harness itself) expose
// against the live session, ordered by Name. Opt-in tests sit out a default run; see SkipOptInTests.
//
// Consumers cannot be loaded by StarMap the normal way: a consumer references HeadlessHarness, but
// StarMap loads mods top-to-bottom and the harness's [StarMapBeforeMain] runs the suite and exits, so
// a consumer listed before the harness would fail to resolve HeadlessHarness (not loaded yet) and one
// listed after never loads. Instead the harness, once it is up, loads consumer DLLs itself into its
// own ALC (so IHarnessTest type identity matches) - a consumer just deploys its DLL to a game mod
// folder and declares a HeadlessHarness dependency in its mod.toml.
//
// Failure accounting: a consumer DLL or type that fails to load is an INFRASTRUCTURE failure, not a
// pass - it usually means the consumer references a renamed/moved game type, which is exactly the
// drift the harness exists to catch. Only a discovered test returning non-zero (or throwing) counts
// as a test failure.
//
// Each test runs under the watchdog with its own budget, and after it the runner puts back what the
// test may have left behind (CleanUpAfterTest), so a test cannot change the start state of the next.
// HarnessResults records every test for the results and JUnit files.
internal static partial class HarnessRunner
{
    // Comma-separated list of test names to run; unset runs every test that is not opt-in. A filter
    // entry that matches no discovered test is an infrastructure failure, as is a filter that names
    // nothing at all (a typo must not silently skip a test).
    public const string TestFilterEnvVar = "KSA_HEADLESS_TESTS";

    public readonly record struct RunResult(int TestFailures, int InfrastructureFailures);

    // The per-test budget of a test that names none (IHarnessTest.TimeoutSeconds null), from
    // KSA_HEADLESS_TEST_TIMEOUT. HarnessMain parses it before the bring-up.
    internal static double? DefaultTestTimeoutSeconds { get; set; }

    // The globals a test may change and the runner puts back after it, as they were before the
    // first test.
    private sealed record RunBaseline(double SimulationSpeed, bool KfsEnabled);

    public static RunResult RunDiscovered(HeadlessSession session)
    {
        int runInfrastructureFailures = LoadConsumerAssemblies();

        List<IHarnessTest> tests = Discover(ref runInfrastructureFailures);
        HashSet<string>? wanted = ParseTestFilter();
        runInfrastructureFailures += ApplyTestFilter(tests, wanted);
        // A filter is the only way to run an opt-in test, so a filtered run has already kept exactly
        // what was named and nothing is left to skip.
        if (wanted == null)
            SkipOptInTests(tests);
        if (tests.Count == 0)
        {
            HarnessLog.Line("[harness] no tests left to run.");
            HarnessResults.RunInfrastructureFailures = runInfrastructureFailures;
            return new RunResult(0, runInfrastructureFailures);
        }

        HarnessLog.Line($"[harness] running {tests.Count} discovered test(s): {string.Join(", ", tests.Select(t => t.Name))}");
        RunBaseline baseline = new(Universe.SimulationSpeed, KfsPrototype.Enabled);
        HeadlessSession.CaptureRunRules();
        int testFailures = 0;
        int testInfrastructureFailures = 0;
        foreach (IHarnessTest test in tests)
        {
            string outcome = RunOne(session, test, baseline, ref runInfrastructureFailures);
            if (outcome == HarnessResults.OutcomeFail)
                testFailures++;
            else if (outcome == HarnessResults.OutcomeInfra)
                testInfrastructureFailures++;
        }

        runInfrastructureFailures += HarnessMods.UnloadAll();
        runInfrastructureFailures += Attempt("the last test", "release the terrain modifier data", () => HeadlessTerrain.ReleaseAll());
        IReadOnlyList<string> unread = TestSupport.UnreadParams();
        if (unread.Count > 0)
            HarnessLog.Line($"[harness] WARNING: {TestSupport.ParamsEnvVar} names {string.Join(", ", unread)}, which no test read " +
                            "(a typo, or the test that reads it did not run).");
        HarnessResults.RunInfrastructureFailures = runInfrastructureFailures;
        return new RunResult(testFailures, runInfrastructureFailures + testInfrastructureFailures);
    }

    // Returns the outcome as the results file records it: pass, fail, skip or infra.
    private static string RunOne(HeadlessSession session, IHarnessTest test, RunBaseline baseline, ref int runInfrastructureFailures)
    {
        HashSet<string> vehiclesBefore = TestSupport.CollectVehicleIds(session.System);
        // Sim seconds is read as the delta of the universe's sim clock across the test: every
        // SimDriver.Step advances Universe.GetElapsedTime by its dt, so a stepping test reports
        // its flown time and a non-stepping test reports 0, with no IHarnessTest surface.
        UniverseTime simBefore = Universe.GetElapsedTime();
        (double? budget, string budgetSource) = TestBudget(test);
        HarnessWatchdog.EnterPhase($"test {test.Name}", budget, budgetSource, () =>
        {
            HarnessResults.BeginTest(test.Name, test.GetType().Assembly.GetName().Name ?? "unknown",
                simBefore.Seconds(), simBefore.Nanoseconds.ToString(CultureInfo.InvariantCulture));
            HarnessRun.CurrentTest = test.Name;
        });
        string budgetText = budget is double b ? $"budget {b:0.###} s from {budgetSource}" : "no time budget";
        HarnessLog.Line($"[harness] start {test.Name} (sim epoch {simBefore.Seconds():F3} s, {budgetText})");

        long startTicks = Stopwatch.GetTimestamp();
        string outcome;
        string? detail = null;
        try
        {
            int code = test.Run(session);
            outcome = OutcomeOfReturn(code);
            if (code == HarnessExit.InfrastructureFailure)
            {
                HarnessLog.Line($"[{test.Name}] INFRA FAIL: the test returned {code}, the environment cannot test what it tests.");
                detail = $"returned {code} (infrastructure failure)";
            }
            else if (code != 0)
            {
                detail = $"returned {code}";
            }
        }
        catch (Exception e) when (IsGameApiDrift(e))
        {
            // A renamed or removed game member surfaces here, not at Discover's GetTypes call: the
            // type loaded, but .NET resolves member references when the calling method JITs, so a
            // game API rename throws inside test.Run. That is exactly the drift the harness exists
            // to catch, so it is an infrastructure failure (results untrustworthy), not a test bug.
            HarnessLog.Line($"[{test.Name}] INFRA FAIL: game API drift:\n{e}");
            outcome = HarnessResults.OutcomeInfra;
            detail = $"game API drift: {e.GetType().Name}: {e.Message}";
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[{test.Name}] FAIL: exception:\n{e}");
            outcome = HarnessResults.OutcomeFail;
            detail = $"exception: {e.GetType().Name}: {e.Message}";
        }
        double wallMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        double simSeconds = (Universe.GetElapsedTime() - simBefore).Seconds();

        HarnessWatchdog.EnterPhase($"cleanup after {test.Name}", budget, budgetSource);
        runInfrastructureFailures += CleanUpAfterTest(test.Name, vehiclesBefore, baseline);
        HarnessRun.CurrentTest = null;
        HarnessWatchdog.ExitPhase($"between tests, after {test.Name}");

        HarnessResults.TestRecord? record = HarnessResults.EndTest(outcome, wallMs, simSeconds, detail);
        string final = record?.Outcome ?? outcome;
        if (record != null && outcome == HarnessResults.OutcomePass && final == HarnessResults.OutcomeFail)
            HarnessLog.Line($"[harness] {test.Name} returned 0 but recorded {record.FailedChecks} failed check(s); counted as a failure.");
        string label = final == HarnessResults.OutcomeInfra ? "INFRA FAIL" : final.ToUpperInvariant();
        HarnessLog.Line($"[harness] {test.Name}: {label} in {wallMs:F0}ms wall, {simSeconds:F1}s sim");
        return final;
    }

    // HarnessExit.InfrastructureFailure is the one return besides 0 that is not a test failure.
    internal static string OutcomeOfReturn(int code) => code switch
    {
        HarnessExit.Pass => HarnessResults.OutcomePass,
        HarnessExit.InfrastructureFailure => HarnessResults.OutcomeInfra,
        _ => HarnessResults.OutcomeFail,
    };

    // A positive IHarnessTest.TimeoutSeconds is the test's own budget, zero or less means no budget
    // for this test, and null takes the run default.
    private static (double? Seconds, string Source) TestBudget(IHarnessTest test)
    {
        int? own = test.TimeoutSeconds;
        if (own is int seconds)
            return seconds > 0 ? (seconds, $"{test.GetType().Name}.TimeoutSeconds") : (null, string.Empty);
        return DefaultTestTimeoutSeconds is double d ? (d, HarnessRun.TestTimeoutEnvVar) : (null, string.Empty);
    }

    // Puts back what a test may leave behind, so the next test starts from the same state: vehicles
    // it spawned and did not despawn (each logged as a LEAK), auto warp and the simulation speed, the
    // run's game rules, PhysicsBubble._forceOffRails, the kFS switch, and the alert list. A loss of
    // control calls Universe.SetSimulationSpeed(1.0), and every alert lands in TimedAlert's static
    // list, which only the UI drains (TimedAlert.DrawList). Returns the number of cleanup steps that
    // threw, each an infrastructure failure, because the tests after it no longer start clean.
    private static int CleanUpAfterTest(string name, HashSet<string> vehiclesBefore, RunBaseline baseline)
    {
        int failures = 0;
        failures += Attempt(name, "discard pending inputs", TestSupport.DiscardPendingInputs);
        CelestialSystem? system = Universe.CurrentSystem;
        if (system != null)
        {
            List<Vehicle> leaked = new();
            for (int i = 0; i < system.Count; i++)
            {
                if (system.GetIndex(i) is Vehicle v && !vehiclesBefore.Contains(v.Id))
                    leaked.Add(v);
            }
            foreach (Vehicle v in leaked)
            {
                HarnessLog.Line($"[harness] LEAK: test '{name}' left vehicle '{v.Id}' behind; despawning it.");
                HarnessResults.AddLeak(v.Id);
                failures += Attempt(name, $"despawn leaked vehicle '{v.Id}'", () => VehicleSpawner.Despawn(v));
            }
        }

        failures += Attempt(name, "reset the simulation speed", () =>
        {
            if (Universe.IsAutoWarpActive)
            {
                HarnessLog.Line($"[harness] test '{name}' left auto warp active; stopping it.");
                Universe.AutoWarpStop(resetSimulationSpeed: false);
            }
            if (Universe.SimulationSpeed != baseline.SimulationSpeed)
            {
                HarnessLog.Line($"[harness] test '{name}' left the simulation speed at {Universe.SimulationSpeed:G6}; " +
                                $"putting back {baseline.SimulationSpeed:G6}.");
                Universe.SetSimulationSpeed(baseline.SimulationSpeed, alert: false);
            }
        });
        failures += Attempt(name, "restore the game rules", () => HeadlessSession.RestoreRunRules($"test {name}"));
        failures += Attempt(name, "restore the terrain modifier data", () => HeadlessTerrain.RestoreRunState($"test '{name}'"));
        failures += Attempt(name, "reset PhysicsBubble._forceOffRails", () =>
        {
            if (PhysicsBubble._forceOffRails)
            {
                HarnessLog.Line($"[harness] test '{name}' left PhysicsBubble._forceOffRails on; turning it off.");
                PhysicsBubble._forceOffRails = false;
            }
        });
        failures += Attempt(name, "restore the kFS switch", () =>
        {
            if (KfsPrototype.Enabled != baseline.KfsEnabled)
            {
                HarnessLog.Line($"[harness] test '{name}' left KfsPrototype.Enabled {(KfsPrototype.Enabled ? "on" : "off")}; putting it back.");
                KfsPrototype.Enabled = baseline.KfsEnabled;
            }
        });
        failures += Attempt(name, "clear the alerts", TimedAlert.ClearAll);
        failures += Attempt(name, "discard cleanup inputs", TestSupport.DiscardPendingInputs);

        IReadOnlyList<string> mods = HarnessMods.Describe();
        if (mods.Count > 0)
            HarnessLog.Line($"[harness] mods still loaded after '{name}': {string.Join(", ", mods)}.");
        return failures;
    }

    private static int Attempt(string test, string step, Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] INFRA FAIL: could not {step} after test '{test}':\n{e}");
            return 1;
        }
    }

    // Member-resolution drift can arrive wrapped, e.g. a TypeInitializationException around a
    // MissingMethodException when a static initializer references a renamed member, so walk the chain.
    private static bool IsGameApiDrift(Exception e)
    {
        for (Exception? cur = e; cur != null; cur = cur.InnerException)
        {
            if (cur is MissingMemberException or TypeLoadException)
                return true;
        }
        return false;
    }

    // Returns the number of load failures (counted as infrastructure failures by the caller).
    private static int LoadConsumerAssemblies()
    {
        Assembly self = typeof(HarnessRunner).Assembly;
        string selfName = self.GetName().Name!;
        // The game builds this path from Constants.DocumentsFolderPath, which StarMap redirects to
        // the instance folder before it loads any mod, so a run in an instance finds its own mods.
        string modsRoot = ModLibrary.LocalModsFolderPath;
        if (!Directory.Exists(modsRoot))
            return 0;

        AssemblyLoadContext alc = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;

        // Index every mod DLL once (name -> path) and resolve a consumer's own dependencies (e.g. a
        // mod under test) against it, instead of re-walking the tree on each unresolved assembly.
        Dictionary<string, string> dllByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string dll in EnumerateFilesSafe(modsRoot, "*.dll"))
        {
            string name = Path.GetFileNameWithoutExtension(dll);
            if (!dllByName.ContainsKey(name))
                dllByName[name] = dll;
        }
        alc.Resolving += (ctx, name) =>
        {
            if (name.Name != null && dllByName.TryGetValue(name.Name, out string? path))
            {
                try { return ctx.LoadFromAssemblyPath(path); }
                catch { /* fall through to default resolution */ }
            }
            return null;
        };

        HashSet<string> loaded = new(AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .OfType<string>());

        int failures = 0;
        foreach (string modDir in EnumerateDirectoriesSafe(modsRoot))
        {
            string tomlPath = Path.Combine(modDir, "mod.toml");
            string toml;
            try
            {
                if (!File.Exists(tomlPath))
                    continue;
                toml = File.ReadAllText(tomlPath);
            }
            catch (Exception e)
            {
                // Unreadable manifest means "is this a consumer?" cannot be answered; treat it as a
                // failure rather than silently skipping a possible consumer.
                HarnessLog.Line($"[harness] FAIL: could not read {tomlPath}: {e.Message}");
                failures++;
                continue;
            }

            // A consumer declares a HeadlessHarness dependency; use that as the "this is a consumer"
            // marker so unrelated deployed mods are left untouched.
            if (!toml.Contains(selfName, StringComparison.Ordinal))
                continue;

            foreach (string dll in EnumerateFilesSafe(modDir, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name == selfName || loaded.Contains(name))
                    continue;
                // A consumer may ship native dependencies next to its managed DLLs; those are not
                // load failures, just not assemblies. Probe first so only a genuinely broken
                // managed assembly counts against the run.
                try
                {
                    AssemblyName.GetAssemblyName(dll);
                }
                catch (BadImageFormatException)
                {
                    continue;
                }
                catch (Exception e)
                {
                    HarnessLog.Line($"[harness] FAIL: could not probe consumer '{name}': {e.Message}");
                    failures++;
                    continue;
                }
                try
                {
                    alc.LoadFromAssemblyPath(dll);
                    loaded.Add(name);
                    HarnessLog.Line($"[harness] loaded consumer assembly '{name}'.");
                }
                catch (Exception e)
                {
                    HarnessLog.Line($"[harness] FAIL: could not load consumer '{name}': {e.Message}");
                    failures++;
                }
            }
        }
        return failures;
    }

    private static List<IHarnessTest> Discover(ref int infrastructureFailures)
    {
        List<IHarnessTest> tests = new();
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A consumer referencing a moved/renamed game type is exactly the drift the harness
                // exists to catch. Count it as a failure, but keep the types that did load so the
                // rest of the suite still runs and the log shows how far it got.
                types = ex.Types.OfType<Type>().ToArray();
                HarnessLog.Line($"[harness] FAIL: partial type load in '{asm.GetName().Name}' ({types.Length} of " +
                                $"{ex.Types.Length} types): {ex.Message}");
                infrastructureFailures++;
            }
            catch (Exception e)
            {
                HarnessLog.Line($"[harness] FAIL: could not read types from '{asm.GetName().Name}': {e.Message}");
                infrastructureFailures++;
                continue;
            }
            foreach (Type type in types)
            {
                if (type.IsAbstract || !typeof(IHarnessTest).IsAssignableFrom(type))
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                    continue;
                try
                {
                    tests.Add((IHarnessTest)Activator.CreateInstance(type)!);
                }
                catch (Exception e)
                {
                    HarnessLog.Line($"[harness] FAIL: could not instantiate {type.FullName}: {e.Message}");
                    infrastructureFailures++;
                }
            }
        }
        tests.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return tests;
    }

    // Null when no filter is set. A set-but-separator-only value ("," or " , ") parses to an empty
    // set, which is kept distinct from null: it names no test, so ApplyTestFilter reports it rather
    // than letting a malformed invocation quietly run the whole suite (or none of it).
    private static HashSet<string>? ParseTestFilter()
    {
        string? filter = Environment.GetEnvironmentVariable(TestFilterEnvVar);
        if (string.IsNullOrWhiteSpace(filter))
            return null;
        return new HashSet<string>(
            filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
    }

    // Returns the number of filter entries that matched no discovered test.
    private static int ApplyTestFilter(List<IHarnessTest> tests, HashSet<string>? wanted)
    {
        if (wanted == null)
            return 0;
        if (wanted.Count == 0)
        {
            HarnessLog.Line($"[harness] FAIL: {TestFilterEnvVar} is set but names no test.");
            tests.Clear();
            return 1;
        }

        int unmatched = 0;
        foreach (string name in wanted)
        {
            if (!tests.Any(t => t.Name == name))
            {
                HarnessLog.Line($"[harness] FAIL: {TestFilterEnvVar} names test '{name}', which was not discovered.");
                unmatched++;
            }
        }
        int skipped = tests.RemoveAll(t => !wanted.Contains(t.Name));
        if (skipped > 0)
            HarnessLog.Line($"[harness] test filter active ({wanted.Count} name(s) from {TestFilterEnvVar}: " +
                            $"{string.Join(", ", wanted)}): skipping {skipped} test(s).");
        return unmatched;
    }

    // Opt-in tests are too expensive for the default suite; naming one in the filter is what runs it,
    // consistent with a named test always running. The skip is logged by name so a deployed opt-in
    // test stays visible instead of silently never running.
    private static void SkipOptInTests(List<IHarnessTest> tests)
    {
        List<string> skipped = new();
        tests.RemoveAll(test =>
        {
            if (!test.OptIn)
                return false;
            skipped.Add(test.Name);
            return true;
        });
        HarnessResults.AddOptInSkipped(skipped);
        if (skipped.Count > 0)
            HarnessLog.Line($"[harness] skipping {skipped.Count} opt-in test(s): {string.Join(", ", skipped)} " +
                            $"(name one in {TestFilterEnvVar} to run it).");
    }

    // GetFiles/GetDirectories walk eagerly, so a mid-walk failure (e.g. a subdir deleted during the
    // scan) is caught here and skips that root instead of escaping the caller's foreach and aborting
    // the whole run. The lazy Enumerate* variants would throw during iteration, outside this try.
    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        try { return Directory.GetFiles(root, pattern, SearchOption.AllDirectories); }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] could not enumerate files under '{root}': {e.Message}");
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] could not enumerate directories under '{root}': {e.Message}");
            return Array.Empty<string>();
        }
    }
}
