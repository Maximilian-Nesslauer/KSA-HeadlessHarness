using System.Globalization;

namespace HeadlessHarness.Core;

// Facts about the running harness that a consumer test may read: which test runs now, and the
// run's seed.
public static class HarnessRun
{
    // An integer a test seeds its own random sources with, so a run can be repeated exactly. The
    // harness records it in the results file and uses it for avionics upsets. Unset uses avionics
    // seed 0; 'off' retains the game's random source.
    public const string SeedEnvVar = "KSA_HEADLESS_SEED";

    // The default wall-clock budget in seconds of every test that does not name its own
    // (IHarnessTest.TimeoutSeconds). Unset or 0: no per-test budget.
    public const string TestTimeoutEnvVar = "KSA_HEADLESS_TEST_TIMEOUT";

    // The wall-clock budget in seconds of the whole run, counted from the harness start. The run
    // script sets it a little below its own kill timeout, so the harness ends a hung run itself and
    // still writes the results. Unset or 0: no run budget.
    public const string RunTimeoutEnvVar = "KSA_HEADLESS_RUN_TIMEOUT";

    // The name of the test that runs now, null between tests.
    public static string? CurrentTest { get; internal set; }

    // Null when KSA_HEADLESS_SEED is unset.
    public static int? Seed => TryParseSeed(Environment.GetEnvironmentVariable(SeedEnvVar), out int? seed) ? seed : null;

    internal static bool TryParseSeed(string? raw, out int? seed)
    {
        seed = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (string.Equals(raw.Trim(), "off", StringComparison.OrdinalIgnoreCase))
            return true;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            seed = value;
            return true;
        }
        return false;
    }

    // Unset, blank or 0: null. Negative or not a number: false.
    internal static bool TryParseSeconds(string? raw, out double? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value < 0.0)
            return false;
        if (value > 0.0)
            seconds = value;
        return true;
    }
}
