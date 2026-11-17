using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Checks the gimbal actuator filter, GimbalAxis.UpdateState, on its own with no vehicle:
//   1. A small step stays in the linear range and follows the critically damped closed form
//      angle(t) = c (1 - (1 + w t) e^(-w t)) without overshoot.
//   2. A full-range step is limited to the slew-rate cap w * MaxAngle / (5 e)
//      (Gimbal.RATE_CAP_PER_NATURAL_FREQUENCY) and still settles on the command.
//   3. The result does not depend on the step size: one call of dt = 1 s against 60 calls of
//      dt = 1/60 s, and the same for a quarter second while the full-range step is rate limited.
public sealed class PropulsionGimbalFilterTest : IHarnessTest
{
    private const float MaxAngle = 0.1f;               // rad, a typical engine gimbal range
    private const float SmallStepFraction = 0.1f;      // of MaxAngle; the linear range ends near 0.2
    private const float ClosedFormTol = 1e-4f;         // of MaxAngle; the filter evaluates exp in float
    private const float StepSizeTol = 1e-3f;           // of MaxAngle
    private const float RateCapTol = 1.0001f;          // the tolerance GimbalAxis allows itself

    public string Name => "gimbal-filter";

    public int Run(HeadlessSession session)
    {
        float omega = Gimbal.DEFAULT_NATURAL_FREQUENCY;
        GimbalAxis axis = new GimbalAxis { MaxAngle = MaxAngle };
        // The constants are compiled in from the referenced game build, so the cap is written out
        // here and the full-step check measures it against what the filter does at run time.
        float cap = omega * MaxAngle / (5f * MathF.E);
        HarnessLog.Line($"[gimbal-filter] w={omega:F4}rad/s MaxAngle={MaxAngle}rad cap={cap:G6}rad/s " +
                        $"(Gimbal.RATE_CAP_PER_NATURAL_FREQUENCY={Gimbal.RATE_CAP_PER_NATURAL_FREQUENCY:G6})");

        bool ok = CheckSmallStep(axis, omega);
        ok &= CheckFullStep(axis, omega, cap);
        ok &= CheckStepSize(axis, omega, SmallStepFraction * MaxAngle, 1f, "small step");
        ok &= CheckStepSize(axis, omega, MaxAngle, 1f, "full step");
        ok &= CheckStepSize(axis, omega, MaxAngle, 0.25f, "full step, rate limited");

        HarnessLog.Line($"[gimbal-filter] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckSmallStep(GimbalAxis axis, float omega)
    {
        float command = SmallStepFraction * MaxAngle;
        const float dt = 1f / 60f;
        float angle = 0f;
        float rate = 0f;
        float worstError = 0f;
        float overshoot = 0f;
        for (int i = 1; i <= 120; i++)
        {
            axis.UpdateState(ref angle, ref rate, command, omega, dt);
            double t = i * (double)dt;
            double closedForm = command * (1.0 - (1.0 + omega * t) * Math.Exp(-omega * t));
            worstError = MathF.Max(worstError, (float)Math.Abs(angle - closedForm));
            overshoot = MathF.Max(overshoot, angle - command);
        }
        bool ok = worstError <= ClosedFormTol * MaxAngle && overshoot <= 0f && angle == command && rate == 0f;
        HarnessLog.Line($"[gimbal-filter] small step to {command:G4}rad: worst error vs closed form {worstError:E2}rad " +
                        $"(tol {ClosedFormTol * MaxAngle:E1}), overshoot {overshoot:E2}rad, final angle {angle:G6} rate {rate:G3} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool CheckFullStep(GimbalAxis axis, float omega, float cap)
    {
        const float dt = 1f / 60f;
        float angle = -MaxAngle;
        float rate = 0f;
        float peakRate = 0f;
        for (int i = 0; i < 120; i++)
        {
            axis.UpdateState(ref angle, ref rate, MaxAngle, omega, dt);
            peakRate = MathF.Max(peakRate, MathF.Abs(rate));
        }
        bool capHeld = peakRate <= RateCapTol * cap;
        bool capReached = peakRate >= 0.99f * cap;
        bool settled = angle == MaxAngle && rate == 0f;
        bool ok = capHeld && capReached && settled;
        HarnessLog.Line($"[gimbal-filter] full step {-MaxAngle:G3}->{MaxAngle:G3}rad: peak rate {peakRate:G6} (cap {cap:G6}), " +
                        $"final angle {angle:G6} rate {rate:G3} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool CheckStepSize(GimbalAxis axis, float omega, float command, float horizon, string label)
    {
        float start = command == MaxAngle ? -MaxAngle : 0f;
        float oneAngle = start;
        float oneRate = 0f;
        axis.UpdateState(ref oneAngle, ref oneRate, command, omega, horizon);

        int steps = (int)MathF.Round(horizon * 60f);
        float manyAngle = start;
        float manyRate = 0f;
        for (int i = 0; i < steps; i++)
            axis.UpdateState(ref manyAngle, ref manyRate, command, omega, horizon / steps);

        float diff = MathF.Abs(oneAngle - manyAngle);
        bool ok = diff <= StepSizeTol * MaxAngle;
        HarnessLog.Line($"[gimbal-filter] {label} over {horizon:G3}s: one call {oneAngle:G6}rad, {steps} calls {manyAngle:G6}rad, " +
                        $"diff {diff:E2} (tol {StepSizeTol * MaxAngle:E1}), rates {oneRate:G4}/{manyRate:G4} => {TestSupport.Verdict(ok)}");
        return ok;
    }
}
