using HeadlessHarness.Core;
using HeadlessHarness.Harness;

namespace HeadlessHarness.SelfTests;

// The log pruning rule on a folder of its own: 205 old run logs with side files, 3 young ones and an
// unrelated file. The newest 200 run logs stay (the 3 young ones among them), so the 8 oldest go
// together with their side files, and the unrelated file stays. The folder is deleted afterwards.
public sealed class RunnerLogPruningTest : IHarnessTest
{
    public string Name => "runner-log-pruning";

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        string dir = Path.Combine(HarnessLog.DataDirectory, $"prune-selftest-pid{Environment.ProcessId}");
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            DateTime old = DateTime.UtcNow - HarnessLogPruner.MinAge - TimeSpan.FromDays(1);
            int oldRuns = HarnessLogPruner.KeepRuns + 5;
            for (int i = 0; i < oldRuns; i++)
            {
                // i = 0 is the oldest run.
                string baseName = $"run-20200101-{i:D6}-pid1";
                DateTime stamp = old - TimeSpan.FromMinutes(oldRuns - i);
                Touch(Path.Combine(dir, baseName + ".log"), stamp);
                Touch(Path.Combine(dir, baseName + "-results.json"), stamp);
                Touch(Path.Combine(dir, baseName + ".game.log"), stamp);
            }
            for (int i = 0; i < 3; i++)
                Touch(Path.Combine(dir, $"run-20990101-{i:D6}-pid2.log"), DateTime.UtcNow);
            Touch(Path.Combine(dir, "v2026.Sol.Test.sig"), old - TimeSpan.FromDays(30));
            string aero = Path.Combine(dir, "run-20200101-000000-pid1-aero");
            Directory.CreateDirectory(aero);
            Touch(Path.Combine(aero, "mesh.obj"), old);
            Directory.SetLastWriteTimeUtc(aero, old);

            HarnessLogPruner.Prune(dir, Path.Combine(dir, "run-current.log"));

            t.Check("the 8 oldest run logs are gone", Enumerable.Range(0, 8).All(i => !File.Exists(Path.Combine(dir, $"run-20200101-{i:D6}-pid1.log"))));
            t.Check("their side files are gone", Enumerable.Range(0, 8).All(i =>
                !File.Exists(Path.Combine(dir, $"run-20200101-{i:D6}-pid1-results.json"))
                && !File.Exists(Path.Combine(dir, $"run-20200101-{i:D6}-pid1.game.log"))));
            t.Check("the newer old runs stay with their side files", Enumerable.Range(8, oldRuns - 8).All(i =>
                File.Exists(Path.Combine(dir, $"run-20200101-{i:D6}-pid1.log"))
                && File.Exists(Path.Combine(dir, $"run-20200101-{i:D6}-pid1-results.json"))));
            t.Check("young run logs stay", Directory.GetFiles(dir, "run-20990101-*.log").Length == 3);
            t.Check("other files stay", File.Exists(Path.Combine(dir, "v2026.Sol.Test.sig")));
            t.Check("expired aero directories are removed", !Directory.Exists(aero));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        return t.Finish();
    }

    private static void Touch(string path, DateTime utc)
    {
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, utc);
    }
}
