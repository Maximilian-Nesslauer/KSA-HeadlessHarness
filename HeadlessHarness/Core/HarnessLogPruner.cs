namespace HeadlessHarness.Core;

// Keeps the harness log folder bounded: the newest KeepRuns run logs stay, and so does every run log
// younger than MinAge, and an older run log goes together with its side files (the results, the
// JUnit file, the data files and the archived game log, which all start with the log's basename).
// Only files named like a run log ("run-*.log") are candidates, so the determinism signatures and
// anything else in the folder are never touched. The caller holds the run mutex, so no other run
// is writing into the folder.
internal static class HarnessLogPruner
{
    public const int KeepRuns = 200;

    public static readonly TimeSpan MinAge = TimeSpan.FromDays(2);

    public static void Prune(string directory, string currentLog)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            // "run-*.log" also matches the archived game log "<run>.game.log", a side file.
            FileInfo[] logs = new DirectoryInfo(directory).GetFiles("run-*.log")
                .Where(f => !Path.GetFileNameWithoutExtension(f.Name).Contains('.'))
                .ToArray();
            Array.Sort(logs, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            DateTime cutoff = DateTime.UtcNow - MinAge;
            string[] all = Directory.GetFiles(directory);
            string[] directories = Directory.GetDirectories(directory);

            int removedRuns = 0, removedFiles = 0;
            for (int i = KeepRuns; i < logs.Length; i++)
            {
                FileInfo log = logs[i];
                if (log.LastWriteTimeUtc > cutoff || string.Equals(log.FullName, Path.GetFullPath(currentLog), StringComparison.OrdinalIgnoreCase))
                    continue;
                string baseName = Path.GetFileNameWithoutExtension(log.Name);
                bool Belongs(string path)
                {
                    string name = Path.GetFileName(path);
                    return name == log.Name
                        || name.StartsWith(baseName + "-", StringComparison.Ordinal)
                        || name.StartsWith(baseName + ".", StringComparison.Ordinal);
                }
                string[] sideFiles = all.Where(Belongs).ToArray();
                string[] sideDirectories = directories.Where(Belongs).ToArray();
                if (sideFiles.Any(file => File.GetLastWriteTimeUtc(file) > cutoff)
                    || sideDirectories.Any(dir => Directory.GetLastWriteTimeUtc(dir) > cutoff
                        || Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Any(file => File.GetLastWriteTimeUtc(file) > cutoff)))
                    continue;
                foreach (string file in sideFiles)
                {
                    try
                    {
                        File.Delete(file);
                        removedFiles++;
                    }
                    catch (Exception e)
                    {
                        HarnessLog.Line($"[harness] log pruning: could not delete '{file}': {e.Message}");
                    }
                }
                foreach (string dir in sideDirectories)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception e) { HarnessLog.Line($"[harness] log pruning: could not delete '{dir}': {e.Message}"); }
                }
                removedRuns++;
            }
            HarnessLog.Line($"[harness] log folder '{directory}': {logs.Length} run log(s), pruned {removedRuns} run(s) " +
                            $"({removedFiles} file(s)); keeps the newest {KeepRuns} and every run younger than {MinAge.TotalDays:0} days.");
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] log pruning of '{directory}' failed: {e.Message}");
        }
    }
}
