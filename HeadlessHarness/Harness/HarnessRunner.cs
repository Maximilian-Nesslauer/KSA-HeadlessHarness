using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HeadlessHarness.Core;

namespace HeadlessHarness.Harness;

// Loads consumer mod assemblies and runs every IHarnessTest they (and the harness itself) expose
// against the live session, ordered by Name.
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
internal static class HarnessRunner
{
    // Comma-separated list of test names to run; unset runs everything. A filter entry that matches
    // no discovered test is an infrastructure failure (a typo must not silently skip a test).
    public const string TestFilterEnvVar = "KSA_HEADLESS_TESTS";

    public readonly record struct RunResult(int TestFailures, int InfrastructureFailures);

    public static RunResult RunDiscovered(HeadlessSession session)
    {
        int infrastructureFailures = LoadConsumerAssemblies();

        List<IHarnessTest> tests = Discover(ref infrastructureFailures);
        infrastructureFailures += ApplyTestFilter(tests);
        if (tests.Count == 0)
        {
            HarnessLog.Line("[harness] no discovered tests.");
            return new RunResult(0, infrastructureFailures);
        }

        HarnessLog.Line($"[harness] running {tests.Count} discovered test(s): {string.Join(", ", tests.Select(t => t.Name))}");
        int testFailures = 0;
        foreach (IHarnessTest test in tests)
        {
            int result;
            try
            {
                result = test.Run(session);
            }
            catch (Exception e)
            {
                HarnessLog.Line($"[{test.Name}] FAIL: exception:\n{e}");
                result = 1;
            }
            if (result != 0)
                testFailures++;
        }
        return new RunResult(testFailures, infrastructureFailures);
    }

    // Returns the number of load failures (counted as infrastructure failures by the caller).
    private static int LoadConsumerAssemblies()
    {
        Assembly self = typeof(HarnessRunner).Assembly;
        string selfName = self.GetName().Name!;
        string modsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "Kitten Space Agency", "mods");
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

    // Returns the number of filter entries that matched no discovered test.
    private static int ApplyTestFilter(List<IHarnessTest> tests)
    {
        string? filter = Environment.GetEnvironmentVariable(TestFilterEnvVar);
        if (string.IsNullOrWhiteSpace(filter))
            return 0;

        HashSet<string> wanted = new(filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
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
            HarnessLog.Line($"[harness] test filter active ({TestFilterEnvVar}={filter}): skipping {skipped} test(s).");
        return unmatched;
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
