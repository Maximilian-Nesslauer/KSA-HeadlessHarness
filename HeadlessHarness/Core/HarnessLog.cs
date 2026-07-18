using Brutal.Logging;

namespace HeadlessHarness.Core;

// Self-contained result log for the harness and its consumer tests. KSA's file logger is wired up
// inside Program..ctor, which the headless harness never runs, so DefaultCategory.Log may have no
// file sink. HarnessLog writes to a per-run file (and console + DefaultCategory best-effort) so a
// run is observable regardless of the game's logging state. Public so consumer IHarnessTest code can
// log to the same place.
//
// Every run gets its own file under DataDirectory (timestamp + pid), so concurrent or successive
// runs never overwrite each other's log. KSA_HEADLESS_LOG overrides the exact file path; the run
// script sets it so it knows which file to print afterwards.
public static class HarnessLog
{
    public const string LogFileEnvVar = "KSA_HEADLESS_LOG";

    // Shared, run-independent home for harness output: per-run logs plus state that must persist
    // across runs (the determinism signatures). Cross-run access is serialized by the run mutex in
    // HarnessMain.
    public static readonly string DataDirectory =
        Path.Combine(Path.GetTempPath(), "ksa-headless-harness");

    public static readonly string FilePath = ResolveFilePath();

    private static string ResolveFilePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(LogFileEnvVar);
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;
        return Path.Combine(DataDirectory,
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.log");
    }

    // Called once at run start: ensure the directories exist and the run's file starts empty (an
    // override path may point at a leftover file from an earlier run). DataDirectory is created
    // separately because an override log path can live elsewhere while the signature files still
    // need their home.
    public static void Prepare()
    {
        try { Directory.CreateDirectory(DataDirectory); } catch { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? DataDirectory);
            File.WriteAllText(FilePath, string.Empty);
        }
        catch { }
    }

    public static void Line(string message)
    {
        try { File.AppendAllText(FilePath, message + Environment.NewLine); } catch { }
        try { Console.WriteLine(message); } catch { }
        try { DefaultCategory.Log.Info(message); } catch { }
    }
}
