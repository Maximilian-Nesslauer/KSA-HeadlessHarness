using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Checks the VehicleSpawner orbit builders against the elements the game's Orbit reports for the
// orbit they build. Pure orbit math around the home body: nothing is spawned or stepped.
public sealed class SpawnOrbitsTest : IHarnessTest
{
    private const double RelTol = 1e-8;
    private const double AngleTol = 1e-8;   // rad

    public string Name => "spawn-orbits";

    public int Run(HeadlessSession session)
    {
        if (session.System.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[spawn-orbits] SKIP: the loaded system has no home body.");
            return 0;
        }
        UniverseTime now = Universe.GetElapsedTime();
        double r0 = home.MeanRadius;
        bool ok = true;

        Orbit circular = VehicleSpawner.CircularAtAltitude(home, 400_000.0, now);
        ok &= Check("circular", Rel(circular.SemiMajorAxis, r0 + 400_000.0) < RelTol && circular.Eccentricity < RelTol,
            $"a={circular.SemiMajorAxis:F3} e={circular.Eccentricity:E2}");

        Orbit elliptical = VehicleSpawner.EllipticalAtAltitudes(home, 300_000.0, 2_000_000.0, now);
        ok &= Check("elliptical", Rel(elliptical.PeriapsisAltitude + r0, r0 + 300_000.0) < RelTol
                                  && Rel(elliptical.ApoapsisAltitude + r0, r0 + 2_000_000.0) < RelTol,
            $"pe={elliptical.PeriapsisAltitude:F3} ap={elliptical.ApoapsisAltitude:F3}");

        const double inc = 0.5, lan = 1.0, argPe = 0.7;
        Orbit inclined = VehicleSpawner.InclinedEllipticalAt(home, 400_000.0, 4_000_000.0, inc, lan, argPe, now);
        ok &= Check("inclined", Math.Abs(inclined.Inclination - inc) < AngleTol
                                && AngleDiff(inclined.LongitudeOfAscendingNode, lan) < AngleTol
                                && AngleDiff(inclined.ArgumentOfPeriapsis, argPe) < AngleTol
                                && Rel(inclined.Periapsis, r0 + 400_000.0) < RelTol
                                && Rel(inclined.Apoapsis, r0 + 4_000_000.0) < RelTol,
            $"i={inclined.Inclination:F12} lan={inclined.LongitudeOfAscendingNode:F12} w={inclined.ArgumentOfPeriapsis:F12} " +
            $"pe={inclined.Periapsis:F3} ap={inclined.Apoapsis:F3}");

        const double vInf = 3_000.0;
        double rp = r0 + 500_000.0;
        Orbit hyperbolic = VehicleSpawner.HyperbolicAt(home, rp, vInf, now);
        StateVectors hsv = hyperbolic.StateVectors;
        double energy = Orbit.GetOrbitalEnergy(in hsv, home.Mu);
        ok &= Check("hyperbolic", hyperbolic.Eccentricity > 1.0 && Rel(energy, vInf * vInf / 2.0) < 1e-6
                                  && Rel(hyperbolic.Periapsis, rp) < 1e-6,
            $"e={hyperbolic.Eccentricity:F6} energy={energy:F3} (expect {vInf * vInf / 2.0:F3}) pe={hyperbolic.Periapsis:F3}");

        StateVectors before = circular.GetStateVectorsAt(now);
        double3 prograde = before.VelocityCci.Normalized() * 100.0;
        Orbit boosted = VehicleSpawner.ApplyImpulse(circular, now, prograde);
        StateVectors after = boosted.GetStateVectorsAt(now);
        ok &= Check("impulse", (after.PositionCci - before.PositionCci).Length() < 1e-6
                               && Math.Abs(after.VelocityCci.Length() - before.VelocityCci.Length() - 100.0) < 1e-6
                               && Rel(boosted.Periapsis, r0 + 400_000.0) < RelTol,
            $"dv={after.VelocityCci.Length() - before.VelocityCci.Length():F9} pe={boosted.Periapsis:F3}");

        HarnessLog.Line($"[spawn-orbits] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool ok, string detail)
    {
        HarnessLog.Line($"[spawn-orbits] {label}: {detail} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static double Rel(double value, double expected) => Math.Abs(value - expected) / Math.Abs(expected);

    private static double AngleDiff(double a, double b)
    {
        double d = Math.IEEERemainder(a - b, 2.0 * Math.PI);
        return Math.Abs(d);
    }
}
