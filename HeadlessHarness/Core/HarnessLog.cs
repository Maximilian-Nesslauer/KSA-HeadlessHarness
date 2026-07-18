using Brutal.Logging;

namespace HeadlessHarness.Core;

// Self-contained result log for the harness and its consumer tests. KSA's file logger is wired up
// inside Program..ctor, which the headless harness never runs, so DefaultCategory.Log may have no
// file sink. HarnessLog writes to a fixed file (and console + DefaultCategory best-effort) so a run
// is observable regardless of the game's logging state. Public so consumer IHarnessTest code can log
// to the same place.
public static class HarnessLog
{
    public static readonly string FilePath =
        Path.Combine(Path.GetTempPath(), "ksa-headless-harness.log");

    public static void Reset()
    {
        try { File.WriteAllText(FilePath, string.Empty); } catch { }
    }

    public static void Line(string message)
    {
        try { File.AppendAllText(FilePath, message + Environment.NewLine); } catch { }
        try { Console.WriteLine(message); } catch { }
        try { DefaultCategory.Log.Info(message); } catch { }
    }
}
