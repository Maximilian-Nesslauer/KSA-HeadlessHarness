using System.Collections.Generic;
using System.Globalization;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;

namespace HeadlessHarness.SelfTests;

// The check context: its counts, its log lines and the exit codes Finish returns, the outcome rule
// of the results file, the run's invariant culture, and the free-form report files. The contexts
// under test write to a list instead of the run log and leave the run's results alone, so their
// failing checks do not fail this test; only this test's own context feeds the results.
public sealed class RunnerChecksTest : IHarnessTest
{
    public string Name => "runner-checks";

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        string? logOverride = Environment.GetEnvironmentVariable(HarnessLog.LogFileEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HarnessLog.LogFileEnvVar, "relative-run.log");
            string resolved = HarnessLog.ResolveFilePath();
            t.Check("filename-only log overrides resolve to an absolute output folder",
                Path.IsPathFullyQualified(resolved) && !string.IsNullOrEmpty(Path.GetDirectoryName(resolved)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HarnessLog.LogFileEnvVar, logOverride);
        }

        List<string> lines = new();
        HarnessChecks probe = new("probe", lines.Add, feedsResults: false);
        t.Check("an empty context passes", probe.Finish() == 0 && probe.CheckCount == 0 && probe.Passed);
        lines.Clear();

        probe.Check("a", true);
        probe.Check("b", false, "detail");
        probe.CheckRel("c", 1.0005, 1.0, 1e-3);
        probe.CheckAbs("d", 2.0, 1.0, 0.5);
        probe.CheckNear("e", 10.1, 10.0, 0.05, 0.01);
        probe.Fail("f", "precondition not met");
        probe.Skip("g");
        probe.Info("note");
        t.Check("counts", probe.CheckCount == 6 && probe.FailureCount == 3 && probe.SkipCount == 1,
            $"checks {probe.CheckCount}, failed {probe.FailureCount}, skipped {probe.SkipCount}");
        int code = probe.Finish();
        t.Check("Finish returns 1 after a failed check", code == 1, $"returned {code}");
        t.Check("check line format", lines.Count == 9 && lines[0] == "[probe] TEST a => PASS"
            && lines[1] == "[probe] TEST b: detail => FAIL", Join(lines));
        t.Check("fail, skip and info line format", lines.Count == 9 && lines[5] == "[probe] TEST f: precondition not met => FAIL"
            && lines[6] == "[probe] SKIP: g" && lines[7] == "[probe] note", Join(lines));
        t.Check("summary line format", lines.Count == 9 && lines[8] == "[probe] 6 check(s), 3 failed, 1 skipped => FAIL", Join(lines));

        List<string> skipLines = new();
        HarnessChecks skipOnly = new("skip-only", skipLines.Add, feedsResults: false);
        skipOnly.Skip("nothing to test here");
        t.Check("a skip alone returns 0 and reads SKIP", skipOnly.Finish() == 0 && skipLines[^1].EndsWith("=> SKIP", StringComparison.Ordinal),
            Join(skipLines));

        List<string> passLines = new();
        HarnessChecks passOnly = new("pass-only", passLines.Add, feedsResults: false);
        passOnly.Check("x", true);
        passOnly.Skip("one case not covered");
        t.Check("a pass with a skip returns 0 and reads PASS", passOnly.Finish() == 0 && passLines[^1].EndsWith("=> PASS", StringComparison.Ordinal),
            Join(passLines));

        t.Check("Near: NaN never matches", !HarnessChecks.Near(double.NaN, double.NaN, 1.0) && !HarnessChecks.NearRel(double.NaN, 1.0, 1.0));
        t.Check("Near: an infinity matches only itself", HarnessChecks.Near(double.PositiveInfinity, double.PositiveInfinity, 0.0)
            && !HarnessChecks.Near(1.0, double.PositiveInfinity, 1e300) && !HarnessChecks.Near(double.NegativeInfinity, double.PositiveInfinity, 0.0));
        t.Check("Near: absolute plus relative tolerance", HarnessChecks.Near(10.1, 10.0, 0.05, 0.01) && !HarnessChecks.Near(10.2, 10.0, 0.05, 0.01));
        t.Check("NearRel: the floor bounds a zero expectation", HarnessChecks.NearRel(1e-12, 0.0, 1e-3, 1e-6)
            && !HarnessChecks.NearRel(1e-3, 0.0, 1e-3, 1e-6) && HarnessChecks.NearRel(0.0, 0.0, 0.0));

        t.Check("outcome: a plain pass", HarnessResults.DecideOutcome(true, 0, 0, 0) == HarnessResults.OutcomePass);
        t.Check("outcome: skips without a check", HarnessResults.DecideOutcome(true, 0, 0, 1) == HarnessResults.OutcomeSkip);
        t.Check("outcome: checks with a skip", HarnessResults.DecideOutcome(true, 2, 0, 1) == HarnessResults.OutcomePass);
        t.Check("outcome: a failed check overrides a 0 return", HarnessResults.DecideOutcome(true, 1, 1, 0) == HarnessResults.OutcomeFail);
        t.Check("outcome: a non-zero return", HarnessResults.DecideOutcome(false, 0, 0, 1) == HarnessResults.OutcomeFail);

        // Read before the Check that records it, so the expectation is this context's count so far.
        int expected = t.CheckCount;
        int counted = HarnessResults.CurrentChecks;
        t.Check("the results count this test's checks and none of the probes'", counted == expected,
            $"results {counted}, context {expected}");

        // A plain log line in the check format and a SKIP line of this test are counted from the log;
        // a line of another test is not.
        HarnessLog.Line($"[{Name}] TEST logged check => PASS");
        HarnessLog.Line($"[{Name}] SKIP: logged skip, which the summary below must not turn into a skip");
        HarnessLog.Line("[some-other-test] SKIP: not this test");
        t.Check("log lines in the check format count as checks", HarnessResults.CurrentLogChecks == 1, $"{HarnessResults.CurrentLogChecks}");
        t.Check("SKIP lines of this test count as skips", HarnessResults.CurrentSkips == 1, $"{HarnessResults.CurrentSkips}");

        string formatted = 4.895e9.ToString("E3") + " " + 1.5.ToString();
        t.Check("the run formats numbers with the invariant culture", formatted == "4.895E+009 1.5"
            && CultureInfo.CurrentCulture.Name.Length == 0, $"'{formatted}', culture '{CultureInfo.CurrentCulture.Name}'");

        HarnessData report = HarnessData.CreateText("runner-checks-report", ".txt");
        try
        {
            report.AppendLine("line one");
            report.AppendLine("line two");
            string text = File.Exists(report.FilePath) ? File.ReadAllText(report.FilePath) : "<missing>";
            string logBase = Path.GetFileNameWithoutExtension(HarnessLog.FilePath);
            t.Check("CreateText writes next to the run log", Path.GetFileName(report.FilePath) == $"{logBase}-runner-checks-report.txt"
                && text == "line one" + Environment.NewLine + "line two" + Environment.NewLine, report.FilePath);
        }
        finally
        {
            try { File.Delete(report.FilePath); } catch { }
        }
        bool refused = false;
        try { HarnessData.CreateText("results", "json"); }
        catch (ArgumentException) { refused = true; }
        t.Check("CreateText refuses the tag of the results file", refused);

        return t.Finish();
    }

    private static string Join(List<string> lines) => string.Join(" | ", lines);
}
