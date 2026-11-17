using System.Globalization;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Loops over Step that tests otherwise write by hand: step until a condition holds, or for a span of
// sim time. Both measure the span on the Elapsed cursor, not by counting steps, so a step that the
// driver shortens still counts only the time it advanced. UniverseTime counts whole nanoseconds, so
// each step rounds dt. The last step takes what is left of the span, which is shorter than dt or, by
// that rounding, a few nanoseconds longer, so a span ends on the nanosecond and no step is a sliver.
public sealed partial class SimDriver
{
    // The largest rounding a span collects over its steps is far below this.
    private const double SpanToleranceSeconds = 1e-6;

    // Steps until condition returns true and returns true, or returns false once maxSimSeconds of sim
    // time have passed without it. The condition is checked before the first step, so a condition
    // that already holds takes no step, and after every step. A timeout writes one log line.
    public bool StepUntil(Func<bool> condition, double dt, double maxSimSeconds)
        => StepUntil(condition, dt, maxSimSeconds, (Func<string>?)null);

    // message names what the test waited for in the timeout line.
    public bool StepUntil(Func<bool> condition, double dt, double maxSimSeconds, string message)
        => StepUntil(condition, dt, maxSimSeconds, () => message);

    // describe is called only on a timeout, so it can format the state at that moment (an altitude,
    // a speed) without a cost on every step.
    public bool StepUntil(Func<bool> condition, double dt, double maxSimSeconds, Func<string>? describe)
    {
        ArgumentNullException.ThrowIfNull(condition);
        CheckStep(dt);
        CheckSpan(maxSimSeconds, nameof(maxSimSeconds));

        UniverseTime start = Elapsed;
        UniverseTime deadline = start + maxSimSeconds;
        if (condition())
            return true;
        while (true)
        {
            double remaining = (deadline - Elapsed).Seconds();
            if (remaining <= 0.0)
                break;
            StepAdvancing(NextStep(dt, remaining));
            if (condition())
                return true;
        }

        double waited = (Elapsed - start).Seconds();
        string what = describe?.Invoke() ?? "condition";
        HarnessLog.Line(string.Create(CultureInfo.InvariantCulture,
            $"[stepping] StepUntil timed out after {waited:F3}s sim (limit {maxSimSeconds:F3}s, dt {dt:G6}s): {what}"));
        return false;
    }

    // Steps for simSeconds of sim time in steps of dt; the last step is shortened to end on the span.
    // perStep runs after every step, for a test that samples the state along the way.
    public void StepFor(double simSeconds, double dt, Action? perStep = null)
    {
        CheckStep(dt);
        CheckSpan(simSeconds, nameof(simSeconds));

        UniverseTime end = Elapsed + simSeconds;
        while (true)
        {
            double remaining = (end - Elapsed).Seconds();
            if (remaining <= 0.0)
                return;
            StepAdvancing(NextStep(dt, remaining));
            perStep?.Invoke();
        }
    }

    private static double NextStep(double dt, double remaining)
        => remaining <= dt + SpanToleranceSeconds ? remaining : dt;

    // A step that left the cursor where it was would make the loops above spin forever.
    private void StepAdvancing(double dt)
    {
        UniverseTime before = Elapsed;
        Step(dt);
        if (Elapsed <= before)
            throw new InvalidOperationException(
                $"[HeadlessHarness] a step of {dt.ToString("G6", CultureInfo.InvariantCulture)}s did not advance the sim time.");
    }

    private static void CheckStep(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "The step must be a positive, finite number of sim seconds.");
    }

    private static void CheckSpan(double seconds, string name)
    {
        if (!double.IsFinite(seconds) || seconds < 0.0)
            throw new ArgumentOutOfRangeException(name, seconds, "The span must be zero or a positive, finite number of sim seconds.");
    }
}
