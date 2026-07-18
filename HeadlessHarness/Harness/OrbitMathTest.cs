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

    public string Name => "orbit-math";

    public int Run(HeadlessSession session)
    {
        if (session.System.HomeBody is not IParentBody home || home is not Astronomical homeBody)
        {
            HarnessLog.Line("[orbit-math] FAIL: the loaded system has no home body.");
            return 1;
        }

        SimTime now = Universe.GetElapsedSimTime();
        double pe = homeBody.MeanRadius + PeriapsisAltitudeM;
        double ap = homeBody.MeanRadius + ApoapsisAltitudeM;
        Orbit orbit = VehicleSpawner.EllipticalCci(home, pe, ap, now);

        SimTime apoTime = orbit.GetNextApoapsisTime(now);
        double3 dv = OrbitalTransfers.DvCciToCircularize(orbit, apoTime);
        StateVectors sv = orbit.GetStateVectorsAt(apoTime);
        Orbit circular = Orbit.CreateFromStateCci(home, apoTime, sv.PositionCci, sv.VelocityCci + dv, VehicleSpawner.OrbitLineColor);

        bool eccOk = circular.Eccentricity < EccentricityTol;
        bool radiusOk = Math.Abs(circular.SemiMajorAxis - ap) / ap < RadiusTol;
        bool ok = eccOk && radiusOk;
        HarnessLog.Line($"[orbit-math] circularize @Ap around '{homeBody.Id}': dv={dv.Length():F3}m/s -> ecc={circular.Eccentricity:F5} SMA={circular.SemiMajorAxis:E4} (target {ap:E4}) => {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
}
