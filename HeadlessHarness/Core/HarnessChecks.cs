namespace HeadlessHarness.Core;

// The check context a test reports through: every check is logged with its verdict and counted, and
// Finish turns the counts into the test's exit code. A headless run leaves nothing behind but its
// log and results file, so a silent pass would look the same as a check an early return never
// reached. The counts of every context a test creates add up into that test's entry of the results
// file, where a test with no check and at least one Skip is reported as skipped.
public sealed class HarnessChecks
{
    private readonly Action<string> _sink;
    private readonly bool _feedsResults;

    public HarnessChecks(string name)
        : this(name, message => HarnessLog.Write(message, observe: false), feedsResults: true)
    {
    }

    // A context with its own sink that leaves the run's results alone, so a self-test can exercise
    // failing checks without failing the test that runs it.
    internal HarnessChecks(string name, Action<string> sink, bool feedsResults)
    {
        Name = name;
        _sink = sink;
        _feedsResults = feedsResults;
    }

    public string Name { get; }

    public int CheckCount { get; private set; }

    public int FailureCount { get; private set; }

    public int SkipCount { get; private set; }

    public bool Passed => FailureCount == 0;

    // Returns the verdict so a caller can branch on it; the check is recorded either way.
    public bool Check(string label, bool pass, string? detail = null)
    {
        Record(pass);
        string body = detail == null ? label : $"{label}: {detail}";
        _sink($"[{Name}] TEST {body} => {(pass ? "PASS" : "FAIL")}");
        return pass;
    }

    // floor is a lower bound under |expected|, so an expectation of zero does not demand bit-exactness.
    public bool CheckRel(string label, double actual, double expected, double relTol, double floor = 0.0)
        => Check(label, NearRel(actual, expected, relTol, floor), $"got {actual:R}, expected {expected:R} (rel {relTol:G3})");

    public bool CheckAbs(string label, double actual, double expected, double absTol)
        => Check(label, Near(actual, expected, absTol), $"got {actual:R}, expected {expected:R} (abs {absTol:G3})");

    public bool CheckNear(string label, double actual, double expected, double absTol, double relTol)
        => Check(label, Near(actual, expected, absTol, relTol), $"got {actual:R}, expected {expected:R} (abs {absTol:G3} + rel {relTol:G3})");

    // A precondition the test could not meet, so what it was going to assert never ran.
    public bool Fail(string label, string detail)
    {
        Record(false);
        _sink($"[{Name}] TEST {label}: {detail} => FAIL");
        return false;
    }

    // Not an assertion: measured values, traces, capability dumps.
    public void Info(string message) => _sink($"[{Name}] {message}");

    // A case this run cannot cover, counted so the summary tells "asserted nothing" from "passed".
    public void Skip(string reason)
    {
        SkipCount++;
        if (_feedsResults)
            HarnessResults.CountSkip();
        _sink($"[{Name}] SKIP: {reason}");
    }

    // 0 when no check failed, 1 otherwise; the value IHarnessTest.Run returns.
    public int Finish()
    {
        _sink($"[{Name}] {CheckCount} check(s), {FailureCount} failed, {SkipCount} skipped => " +
              $"{(Passed ? (CheckCount == 0 && SkipCount > 0 ? "SKIP" : "PASS") : "FAIL")}");
        return Passed ? 0 : 1;
    }

    // |actual - expected| <= absTol + relTol * |expected|. A non-finite value matches only itself,
    // so a NaN never passes and an infinity passes only against the same infinity.
    public static bool Near(double actual, double expected, double absTol, double relTol = 0.0)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected))
            return actual.Equals(expected) && !double.IsNaN(actual);
        return Math.Abs(actual - expected) <= absTol + relTol * Math.Abs(expected);
    }

    public static bool NearRel(double actual, double expected, double relTol, double floor = 0.0)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected))
            return actual.Equals(expected) && !double.IsNaN(actual);
        return Math.Abs(actual - expected) <= relTol * Math.Max(Math.Abs(expected), floor);
    }

    private void Record(bool pass)
    {
        CheckCount++;
        if (!pass)
            FailureCount++;
        if (_feedsResults)
            HarnessResults.CountCheck(pass);
    }
}
