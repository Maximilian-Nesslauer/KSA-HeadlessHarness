using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// The harness entry, run by Mod.OnBeforeMain: brings the game up GPU-free, validates the session
// with content-agnostic smoke checks, then runs every discovered IHarnessTest (the harness's own
// plus consumer tests). Returns a process exit code; the process exits before KSA Program.Main.
//
// Exit codes: 0 = all tests passed; 1 = at least one test failed; 2 = infrastructure failure
// (bring-up broke, KSA_HEADLESS_SYSTEM named an unknown system, the session did not validate, a
// consumer assembly failed to load, or the run mutex could not be acquired), meaning the test
// results are not trustworthy; 3 = the watchdog ended a test or the run that outlived its budget.
// Every run also writes <log basename>-results.json and <log basename>-junit.xml next to its log.
//
// Runs are serialized machine-wide through a named mutex, so several sessions can invoke the
// harness concurrently and simply queue: cross-run state (the determinism signatures in
// HarnessLog.DataDirectory) is never accessed by two runs at once, regardless of how the game was
// launched. The run script holds its own, differently named mutex around the manifest swap; the two
// must not share a name or a scripted run would deadlock against its own game process.
internal static partial class HarnessMain
{
    public const int ExitPass = 0;
    public const int ExitTestFailure = 1;
    public const int ExitInfrastructureFailure = 2;

    private const string RunMutexName = @"Global\KSA-HeadlessHarness";

    // Must stay below the run script's game timeout (at least 120s): a scripted game queued behind a direct
    // launch then reports a loud "could not acquire the run mutex" exit 2 instead of being killed
    // by the script and mislabeled a timeout.
    private static readonly TimeSpan RunMutexTimeout = TimeSpan.FromSeconds(90);

    // A test or the whole run outlived its watchdog budget (IHarnessTest.TimeoutSeconds,
    // KSA_HEADLESS_TEST_TIMEOUT, KSA_HEADLESS_RUN_TIMEOUT). The run script uses the same code when it
    // has to kill a run that did not end itself.
    public const int ExitTimeout = 3;

    // Why the run ended as an infrastructure failure, for the results file.
    private static string? _runNote;

    public static int Run()
    {
        // The game and every test format numbers with the current culture, and a log or CSV written
        // on a machine with a decimal comma would not parse elsewhere.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        HarnessLog.Prepare();
        HarnessLog.Line($"[harness] start (log: {HarnessLog.FilePath})");
        HarnessResults.GameVersion = VersionInfo.Current.VersionString;
        HarnessResults.HarnessVersion = typeof(HarnessMain).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HarnessMain).Assembly.GetName().Version?.ToString();

        if (!HarnessRun.TryParseSeconds(Environment.GetEnvironmentVariable(HarnessRun.RunTimeoutEnvVar), out double? runBudget))
            return FailBeforeLock($"{HarnessRun.RunTimeoutEnvVar} is not a number of seconds.");
        HarnessWatchdog.Start(runBudget, OnWatchdogExpired);
        if (runBudget is double rb)
            HarnessLog.Line($"[harness] run budget {rb:0.###} s ({HarnessRun.RunTimeoutEnvVar}).");

        using Mutex runMutex = new Mutex(false, RunMutexName);
        bool acquired;
        try
        {
            HarnessLog.Line("[harness] acquiring the machine-wide run mutex (another harness run may be in progress)...");
            HarnessWatchdog.ExitPhase("waiting for the run mutex");
            acquired = runMutex.WaitOne(RunMutexTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-run; the mutex is ours. The only cross-run state is the
            // signature files, and a truncated one from a dead writer just logs a one-time spurious
            // DIFFER and self-heals on the next write.
            acquired = true;
        }
        if (!acquired)
            return FailBeforeLock($"could not acquire the run mutex within {RunMutexTimeout.TotalSeconds:F0}s (another harness run is holding it).");
        int exitCode = ExitInfrastructureFailure;
        try
        {
            PruneLogFolders();
            exitCode = RunLocked();
            return exitCode;
        }
        finally
        {
            HarnessWatchdog.Stop();
            HarnessResults.Write(exitCode, exitCode == ExitInfrastructureFailure ? _runNote : null);
            HarnessLog.Line($"[harness] results: {HarnessResults.ResultsPath}");
            HarnessLog.Line($"[harness] junit: {HarnessResults.JUnitPath}");
            runMutex.ReleaseMutex();
        }
    }

    private static int FailBeforeLock(string message)
    {
        HarnessLog.Line($"[harness] INFRA FAIL: {message}");
        HarnessResults.Write(ExitInfrastructureFailure, message);
        return ExitInfrastructureFailure;
    }

    private static int Infra(string message)
    {
        _runNote ??= message;
        HarnessLog.Line($"[harness] INFRA FAIL: {message}");
        return ExitInfrastructureFailure;
    }

    // Runs on the watchdog thread while the test thread is still stuck, so it records the hung test,
    // writes the results and ends the process without waiting for the test thread.
    private static void OnWatchdogExpired(string message)
    {
        HarnessLog.Line($"[harness] WATCHDOG: {message}. Writing the results and ending the run with exit code {ExitTimeout}.");
        string? test = HarnessResults.MarkCurrentTimedOut($"watchdog: {message}");
        if (test != null)
            HarnessLog.Line($"[harness] {test}: TIMEOUT");
        HarnessResults.Write(ExitTimeout, $"watchdog: {message}");
        HarnessLog.Line($"[harness] results: {HarnessResults.ResultsPath}");
        Environment.Exit(ExitTimeout);
    }

    private static void PruneLogFolders()
    {
        HarnessLogPruner.Prune(HarnessLog.DataDirectory, HarnessLog.FilePath);
        string? logDir = Path.GetDirectoryName(Path.GetFullPath(HarnessLog.FilePath));
        if (logDir != null && !string.Equals(Path.GetFullPath(HarnessLog.DataDirectory).TrimEnd('\\', '/'),
                logDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            HarnessLogPruner.Prune(logDir, HarnessLog.FilePath);
    }

    // The run settings that only the harness reads, checked before the long bring-up so a typo fails
    // at once instead of running the suite with defaults.
    private static bool ReadRunSettings()
    {
        string? seed = Environment.GetEnvironmentVariable(HarnessRun.SeedEnvVar);
        if (!HarnessRun.TryParseSeed(seed, out int? seedValue))
        {
            Infra($"{HarnessRun.SeedEnvVar}='{seed}' is neither an integer nor 'off'.");
            return false;
        }
        HarnessResults.Seed = seedValue?.ToString(CultureInfo.InvariantCulture);
        if (seedValue != null)
            HarnessLog.Line($"[harness] seed {seedValue} ({HarnessRun.SeedEnvVar}).");

        string? testTimeout = Environment.GetEnvironmentVariable(HarnessRun.TestTimeoutEnvVar);
        if (!HarnessRun.TryParseSeconds(testTimeout, out double? testBudget))
        {
            Infra($"{HarnessRun.TestTimeoutEnvVar}='{testTimeout}' is not a number of seconds.");
            return false;
        }
        HarnessRunner.DefaultTestTimeoutSeconds = testBudget;
        if (testBudget != null)
            HarnessLog.Line($"[harness] default test budget {testBudget:0.###} s ({HarnessRun.TestTimeoutEnvVar}).");

        string? parameters = Environment.GetEnvironmentVariable(TestSupport.ParamsEnvVar);
        try
        {
            Dictionary<string, string> parsed = TestSupport.ParseParams(parameters);
            if (parsed.Count > 0)
            {
                HarnessResults.Params = parameters;
                HarnessLog.Line($"[harness] params ({TestSupport.ParamsEnvVar}): " +
                                string.Join("; ", parsed.Select(kv => $"{kv.Key}={kv.Value}")));
            }
        }
        catch (InvalidOperationException e)
        {
            Infra(e.Message);
            return false;
        }

        string? filter = Environment.GetEnvironmentVariable(HarnessRunner.TestFilterEnvVar);
        HarnessResults.TestFilter = string.IsNullOrWhiteSpace(filter) ? null : filter;
        string? rules = Environment.GetEnvironmentVariable(HeadlessSession.RulesEnvVar);
        HarnessResults.RulesRequest = string.IsNullOrWhiteSpace(rules) ? null : rules;
        string? instance = Environment.GetEnvironmentVariable("STARMAP_INSTANCE_PATH");
        HarnessResults.InstancePath = string.IsNullOrWhiteSpace(instance) ? null : instance;
        HarnessResults.ProfilePath = Constants.DocumentsFolderPath;
        return true;
    }

    private static int RunLocked()
    {
        if (!ReadRunSettings())
            return ExitInfrastructureFailure;

        HeadlessSession session = new HeadlessSession();
        string? systemId = Environment.GetEnvironmentVariable(HeadlessSession.SystemEnvVar);
        systemId = string.IsNullOrWhiteSpace(systemId) ? null : systemId.Trim();
        if (systemId != null)
            HarnessLog.Line($"[harness] {HeadlessSession.SystemEnvVar} selects system '{systemId}'.");
        HarnessWatchdog.ExitPhase("bring-up");
        try
        {
            session.BringUp(systemId);
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] INFRA FAIL: exception during bring-up:\n{e}");
            _runNote ??= $"bring-up failed: {e.GetType().Name}: {e.Message}";
            return ExitInfrastructureFailure;
        }
        HarnessResults.SystemId = Universe.CurrentSystem?.Id;
        HarnessResults.Radiation = Universe.Rules.Radiation;
        HarnessResults.LifeSupport = Universe.Rules.LifeSupport;

        try
        {
            HarnessWatchdog.ExitPhase("session validation");
            if (!ValidateSession(session))
            {
                _runNote ??= "the session did not validate (see the [smoke] lines of the run log)";
                return ExitInfrastructureFailure;
            }

            HarnessRunner.RunResult result = HarnessRunner.RunDiscovered(session);
            if (result.InfrastructureFailures > 0)
            {
                HarnessLog.Line($"[harness] overall: INFRA FAIL ({result.InfrastructureFailures} infrastructure failure(s), " +
                                $"{result.TestFailures} test failure(s)).");
                _runNote ??= $"{result.InfrastructureFailures} infrastructure failure(s); see the run log";
                return ExitInfrastructureFailure;
            }
            HarnessLog.Line($"[harness] overall: {(result.TestFailures == 0 ? "ALL PASS" : $"{result.TestFailures} test failure(s)")}.");
            return result.TestFailures == 0 ? ExitPass : ExitTestFailure;
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] INFRA FAIL: unexpected exception outside any test:\n{e}");
            _runNote ??= $"unexpected exception outside any test: {e.GetType().Name}: {e.Message}";
            return ExitInfrastructureFailure;
        }
    }

    // Content-agnostic checks that the GPU-free session is genuinely alive: the game's viewport
    // registry answers with the headless viewport, bodies loaded, a world sun resolved, and the
    // solvers tick (an orbiting body moves). No stock entity names, so this holds for any star
    // system a mod setup loads.
    private static bool ValidateSession(HeadlessSession session)
    {
        if (!ValidateViewport(session))
            return false;

        CelestialSystem system = session.System;
        if (system.Count <= 0)
        {
            HarnessLog.Line("[smoke] INFRA FAIL: system has no bodies.");
            return false;
        }
        if (Universe.WorldSun == null)
        {
            HarnessLog.Line("[smoke] INFRA FAIL: no world sun resolved.");
            return false;
        }

        LogInventory(system, out Celestial? mover);

        SimDriver driver = session.CreateDriver();
        driver.StepOrbits = true;
        if (mover != null)
        {
            double3 before = mover.Orbit.StateVectors.PositionCci;
            driver.Step(3600.0, 24); // one day, hourly steps
            double moved = (mover.Orbit.StateVectors.PositionCci - before).Length();
            if (moved <= 0.0)
            {
                HarnessLog.Line($"[smoke] INFRA FAIL: '{mover.Id}' did not move over one simulated day.");
                return false;
            }
            HarnessLog.Line($"[smoke] solver tick GPU-free: '{mover.Id}' moved {moved:E3}m over 1 day.");
        }
        else
        {
            HarnessLog.Line("[smoke] no orbiting celestial besides the world sun; skipping the solver movement check.");
        }

        HarnessLog.Line("[smoke] PASS (GPU-free system load + solver tick).");
        return true;
    }

    // RegisterHeadlessViewport already asserts Program.MainViewport, so what is left is the accessor
    // the sim path calls: Vehicle.PrepareWorker reaches Program.GetMainCamera, and an empty registry
    // throws there rather than returning null, deep inside whichever test hits it first.
    private static bool ValidateViewport(HeadlessSession session)
    {
        HeadlessViewport? viewport = session.MainViewport;
        if (viewport == null)
        {
            HarnessLog.Line("[smoke] INFRA FAIL: bring-up registered no headless viewport.");
            return false;
        }
        if (!ReferenceEquals(Program.GetMainCamera(), viewport.BaseCamera))
        {
            HarnessLog.Line("[smoke] INFRA FAIL: Program.GetMainCamera() is not the headless viewport's base camera.");
            return false;
        }

        HarnessLog.Line($"[smoke] headless viewport live: '{viewport.Name}' {viewport.Width}x{viewport.Height}, " +
                        $"mode {viewport.Mode}, {ViewportRegistry.GameViews.Length} game view(s) registered.");
        return true;
    }

    private static void LogInventory(CelestialSystem system, out Celestial? mover)
    {
        SortedDictionary<string, int> histogram = new();
        int vehicles = 0;
        mover = null;
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Astronomical a)
                continue;
            string type = a.GetType().Name;
            histogram[type] = histogram.GetValueOrDefault(type) + 1;
            if (a is Vehicle)
                vehicles++;
            if (a is Celestial c && !ReferenceEquals(c, Universe.WorldSun) && mover == null)
                mover = c;
        }

        HarnessLog.Line($"[smoke] system '{system.Id}' loaded GPU-free: {system.Count} bodies " +
                        $"({vehicles} vehicles), world sun '{Universe.WorldSun?.Id}'.");

        // CelestialSystem..ctor catches an exception per root tree, so a root that failed to build
        // shows up here only as a missing id.
        ReadOnlySpan<IIndependentRoot> roots = Universe.Roots;
        List<string> rootIds = new List<string>(roots.Length);
        foreach (IIndependentRoot root in roots)
            rootIds.Add(root is Astronomical a ? a.Id : root.GetType().Name);
        HarnessLog.Line($"[smoke] {roots.Length} independent root(s): {string.Join(", ", rootIds)}.");
        foreach (KeyValuePair<string, int> kv in histogram)
            HarnessLog.Line($"[smoke]   {kv.Value,3} x {kv.Key}");
    }
}
