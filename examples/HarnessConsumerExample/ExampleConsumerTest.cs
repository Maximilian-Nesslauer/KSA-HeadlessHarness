using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// A test that lives in a SEPARATE mod and plugs into the harness purely by implementing IHarnessTest
// and referencing HeadlessHarness. It validates circularization at PERIAPSIS (complementing the
// harness's own apoapsis check) against the game's real orbit propagation. A real consumer test looks
// exactly like this, additionally referencing the mod under test and calling its own guidance code.
public sealed class ExampleConsumerTest : IHarnessTest
{
    // An arbitrary elliptical test orbit (altitudes in meters above the home body's mean radius).
    private const double PeriapsisAltitudeM = 300_000.0;
    private const double ApoapsisAltitudeM = 5_000_000.0;
    private const double EccentricityTol = 1e-3;
    private const double RadiusTol = 1e-3; // relative

    private static readonly byte4 OrbitColor = new byte4(255, 255, 255, 255);

    public string Name => "example-consumer";

    public int Run(HeadlessSession session)
    {
        if (session.System.HomeBody is not IParentBody home || home is not Astronomical homeBody)
        {
            HarnessLog.Line("[example-consumer] FAIL: the loaded system has no home body.");
            return 1;
        }

        SimTime now = Universe.GetElapsedSimTime();
        double pe = homeBody.MeanRadius + PeriapsisAltitudeM;
        double ap = homeBody.MeanRadius + ApoapsisAltitudeM;
        Orbit orbit = VehicleSpawner.EllipticalCci(home, pe, ap, now);

        SimTime peTime = orbit.GetNextPeriapsisTime(now);
        double3 dv = OrbitalTransfers.DvCciToCircularize(orbit, peTime);
        StateVectors sv = orbit.GetStateVectorsAt(peTime);
        Orbit circular = Orbit.CreateFromStateCci(home, peTime, sv.PositionCci, sv.VelocityCci + dv, OrbitColor);

        bool ok = circular.Eccentricity < EccentricityTol && Math.Abs(circular.SemiMajorAxis - pe) / pe < RadiusTol;
        HarnessLog.Line($"[example-consumer] circularize @Pe around '{homeBody.Id}': dv={dv.Length():F3}m/s -> ecc={circular.Eccentricity:F5} SMA={circular.SemiMajorAxis:E4} (target {pe:E4}) => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }
}
