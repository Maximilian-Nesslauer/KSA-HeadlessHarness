using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Validates orbital-maneuver math against the game's own orbit propagation, with no sim step: build
// an elliptical orbit around the system's home body, compute the circularization dV via the stock
// OrbitalTransfers.DvCciToCircularize, apply the impulse to the apoapsis state, and confirm the
// resulting orbit is circular at the apoapsis radius. It doubles as a worked example of the round-trip
// a consumer test performs against a guidance function: build an orbit, compute a maneuver, apply it,
// assert the result.
public sealed class OrbitMathTest : IHarnessTest
{
    // An arbitrary elliptical low test orbit (altitudes in meters above the home body's mean radius).
    private const double PeriapsisAltitudeM = 400_000.0;
    private const double ApoapsisAltitudeM = 2_400_000.0;
    private const double EccentricityTol = 1e-3;
    private const double RadiusTol = 1e-3; // relative

    private static readonly double[] NearParabolicEccentricities = { 1.0 - 1e-4, 1.0 - 1e-12, 1.0 + 1e-12, 1.0 + 1e-4 };
    private const double NearParabolicRadiusFactor = 2.0;  // the outbound crossing, in periapsis radii
    private const double NearParabolicRadiusTol = 1e-5;    // relative; propagated position at the crossing time
    private const double NearParabolicSpeedTol = 1e-6;     // relative; vis-viva

    public string Name => "orbit-math";

    public int Run(HeadlessSession session)
    {
        if (session.System.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[orbit-math] FAIL: the loaded system has no home body.");
            return 1;
        }

        UniverseTime now = Universe.GetElapsedTime();
        double pe = home.MeanRadius + PeriapsisAltitudeM;
        double ap = home.MeanRadius + ApoapsisAltitudeM;
        Orbit orbit = VehicleSpawner.EllipticalCci(home, pe, ap, now);

        // A bounded ellipse always reaches apoapsis, so a null here means the game's anomaly solver
        // could not place it: report the drift instead of feeding a substitute time into the maneuver.
        if (orbit.GetNextApoapsisTime(now) is not UniverseTime apoTime)
        {
            HarnessLog.Line($"[orbit-math] FAIL: no next apoapsis for the test orbit around '{home.Id}' (ecc={orbit.Eccentricity:F5}).");
            return 1;
        }
        double3 dv = OrbitalTransfers.DvCciToCircularize(orbit, apoTime);
        StateVectors sv = orbit.GetStateVectorsAt(apoTime);
        Orbit circular = Orbit.CreateFromStateCci(home, apoTime, sv.PositionCci, sv.VelocityCci + dv, VehicleSpawner.OrbitLineColor);

        bool eccOk = circular.Eccentricity < EccentricityTol;
        bool radiusOk = Math.Abs(circular.SemiMajorAxis - ap) / ap < RadiusTol;
        bool ok = eccOk && radiusOk;
        HarnessLog.Line($"[orbit-math] circularize @Ap around '{home.Id}': dv={dv.Length():F3}m/s -> ecc={circular.Eccentricity:F5} SMA={circular.SemiMajorAxis:E4} (target {ap:E4}) => {TestSupport.Verdict(ok)}");

        foreach (double eccentricity in NearParabolicEccentricities)
            ok &= CheckNearParabolic(home, now, eccentricity);
        return ok ? 0 : 1;
    }

    // Orbit.GetOrbitType calls an orbit parabolic when its energy is within a relative 1e-6 of zero,
    // so an orbit with e = 1 +- 1e-12 is an Unbound Parabolic, e = 1 - 1e-4 a Bound ellipse with an
    // enormous period and e = 1 + 1e-4 a Hyperbolic. GetNextTimeOfRadius, GetVelocityCciAtRadius and
    // the apsis times branch on Orbit.IsBound, and each branch must give finite answers that agree with
    // vis-viva.
    //
    // The crossing time must also put the propagated state (GetStateVectorsAt) on the radius, except
    // on a Parabolic orbit: there GetNextTimeOfRadius goes through TimeOfTrueAnomaly and
    // Orbit.Parabolic.GetTimeFromPeTo(EccentricAnomaly), which returns half the time of flight of
    // Orbit.Parabolic.GetTimeFromPeTo(TrueAnomaly) and of the propagation. The mismatch is logged as a
    // game defect there instead of failing the suite, so the line shows when a game build fixes it.
    private static bool CheckNearParabolic(IParentBody home, UniverseTime now, double eccentricity)
    {
        double pe = home.MeanRadius + PeriapsisAltitudeM;
        double speedPe = Math.Sqrt(home.Mu * (1.0 + eccentricity) / pe);
        Orbit orbit = Orbit.CreateFromStateCci(home, now, new double3(pe, 0.0, 0.0), new double3(0.0, speedPe, 0.0), VehicleSpawner.OrbitLineColor);
        double radius = NearParabolicRadiusFactor * pe;
        double expectedSpeed = Math.Sqrt(speedPe * speedPe - 2.0 * home.Mu / pe + 2.0 * home.Mu / radius);

        (UniverseTime Time, TrueAnomaly Anomaly)? crossing = orbit.GetNextTimeOfRadius(now, new Radius(radius));
        double3? velocity = orbit.GetVelocityCciAtRadius(new Radius(radius));
        UniverseTime? nextPe = orbit.GetNextPeriapsisTime(now);
        UniverseTime? nextAp = orbit.GetNextApoapsisTime(now);
        double sincePe = orbit.GetTimeSincePeriapsisThisOrbit(now + 100.0).Seconds();

        double crossingSeconds = crossing is { } c ? (c.Time - now).Seconds() : double.NaN;
        bool crossingOk = double.IsFinite(crossingSeconds) && crossingSeconds > 0.0;
        double reachedRadius = crossingOk ? orbit.GetStateVectorsAt(crossing!.Value.Time).PositionCci.Length() : double.NaN;
        bool radiusOk = Math.Abs(reachedRadius - radius) <= NearParabolicRadiusTol * radius;
        double speed = velocity is { } v && double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z) ? v.Length() : double.NaN;
        bool velocityOk = Math.Abs(speed - expectedSpeed) <= NearParabolicSpeedTol * expectedSpeed;
        double nextPeSeconds = nextPe?.Seconds() ?? double.NaN;
        double nextApSeconds = nextAp?.Seconds() ?? double.NaN;
        bool apsesOk = double.IsFinite(nextPeSeconds) && double.IsFinite(nextApSeconds) && double.IsFinite(sincePe);
        bool parabolic = orbit is Orbit.Parabolic;
        bool ok = crossingOk && (radiusOk || parabolic) && velocityOk && apsesOk;
        HarnessLog.Line($"[orbit-math] e=1{eccentricity - 1.0:+0.0E+0;-0.0E+0} -> {orbit.GetType().Name} (bound={orbit.IsBound()}, ecc={orbit.Eccentricity:R}): " +
                        $"r={radius:E4} at t+{crossingSeconds:F1}s reached {reachedRadius:E6}, speed {speed:F3}m/s (vis-viva {expectedSpeed:F3}), " +
                        $"next Pe {nextPeSeconds:F1} next Ap {nextApSeconds:F1}, since Pe {sincePe:F1}s => {TestSupport.Verdict(ok)}");
        if (parabolic && !radiusOk)
            HarnessLog.Line($"[orbit-math]   game defect: the propagated state at the reported crossing time is at {reachedRadius / radius:F4} of the radius " +
                            "(Orbit.Parabolic.GetTimeFromPeTo(EccentricAnomaly) gives half the time of flight); not asserted.");
        return ok;
    }
}
