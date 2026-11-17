using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Checks SimDriver.StepUntil and StepFor on the loaded system without a vehicle of its own: the
// condition is checked before the first step, a span ends on the nanosecond, the last step is
// shortened, a timeout returns false after exactly the limit, and the universe clock follows the
// driver. The steps are a few seconds of sim time, so the test is cheap with any system.
public sealed class SteppingUntilTest : IHarnessTest
{
    public string Name => "stepping-until";

    public int Run(HeadlessSession session)
    {
        SimDriver driver = session.CreateDriver();
        bool ok = true;

        // A condition that already holds takes no step.
        UniverseTime t0 = driver.Elapsed;
        int checks = 0;
        bool met = driver.StepUntil(() => { checks++; return true; }, 1.0, 10.0);
        ok &= Check("condition already true", met && driver.Elapsed == t0 && checks == 1,
            $"returned {met}, advanced {Text(driver.Elapsed - t0)}s, {checks} check(s)");

        // A condition that holds after five steps stops right there.
        t0 = driver.Elapsed;
        checks = 0;
        met = driver.StepUntil(() => { checks++; return (driver.Elapsed - t0).Seconds() >= 5.0; }, 1.0, 60.0, "five seconds of sim time");
        ok &= Check("condition after 5 s", met && driver.Elapsed == t0 + 5.0 && checks == 6,
            $"returned {met}, advanced {Text(driver.Elapsed - t0)}s, {checks} check(s)");

        // A timeout returns false after exactly the limit, with the last step shortened (0.4, 0.4, 0.2).
        t0 = driver.Elapsed;
        int steps = 0;
        met = driver.StepUntil(() => { steps++; return false; }, 0.4, 1.0, () => "a condition that never holds (expected timeout)");
        ok &= Check("timeout", !met && driver.Elapsed == t0 + 1.0 && steps == 4,
            $"returned {met}, advanced {Text(driver.Elapsed - t0)}s, {steps - 1} step(s)");

        // StepFor with a remainder: 1 + 1 + 0.5 s, perStep after each.
        t0 = driver.Elapsed;
        steps = 0;
        driver.StepFor(2.5, 1.0, () => steps++);
        ok &= Check("StepFor 2.5 s by 1 s", driver.Elapsed == t0 + 2.5 && steps == 3,
            $"advanced {Text(driver.Elapsed - t0)}s in {steps} step(s)");

        // A step that does not divide the span: the rounding of each step to whole nanoseconds is
        // taken up by the last one, so the span still ends on the nanosecond with three steps.
        t0 = driver.Elapsed;
        steps = 0;
        driver.StepFor(1.0, 1.0 / 3.0, () => steps++);
        ok &= Check("StepFor 1 s by 1/3 s", driver.Elapsed == t0 + 1.0 && steps == 3,
            $"advanced {(driver.Elapsed - t0).Nanoseconds}ns in {steps} step(s)");

        // A zero span takes no step.
        t0 = driver.Elapsed;
        steps = 0;
        driver.StepFor(0.0, 1.0, () => steps++);
        ok &= Check("StepFor 0 s", driver.Elapsed == t0 && steps == 0, $"{steps} step(s)");

        ok &= Check("universe clock follows the driver", Universe.GetElapsedTime() == driver.Elapsed,
            $"universe {Universe.GetElapsedTime().Seconds():F3}s, driver {driver.Elapsed.Seconds():F3}s");

        ok &= Check("a step of 0 s is refused", Throws<ArgumentOutOfRangeException>(() => driver.StepFor(1.0, 0.0)), "");
        ok &= Check("a negative limit is refused", Throws<ArgumentOutOfRangeException>(() => driver.StepUntil(() => true, 1.0, -1.0)), "");

        driver.StepOrbits = true;
        driver.Step(1.0);
        if (session.System.HomeBody is Celestial body)
            ok &= Check("celestial frame cache follows the applied orbit",
                body.GetCcf2Cci().Equals(body.GetCcf2Cci(body.Orbit.StateVectors.StateTime)), "");

        HarnessLog.Line($"[stepping-until] => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static string Text(UniverseTime span) => span.Seconds().ToString("F9", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Check(string what, bool pass, string detail)
    {
        HarnessLog.Line($"[stepping-until] {what}: {detail} => {TestSupport.Verdict(pass)}");
        return pass;
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
