using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The worked example of an OPT-IN test that also emits a HarnessData CSV: a sweep that repeats one
// round-trip over a range of orbits and records each sample as a row. A default run skips it (and
// says so); it runs only when KSA_HEADLESS_TESTS names it. A real sweep flies vehicles and takes
// minutes to hours, which is why it must stay off the default suite; this one keeps to orbit math so
// the mechanism is cheap to demonstrate. It is a shape to copy, not a coverage gain: circularizing at
// a sample's own radius is true by construction, so the per-sample assertion stands in for the
// measurements a real sweep would record, and the CSV stands in for the dataset it would produce.
public sealed class ExampleSweepTest : IHarnessTest
{
    // Altitudes in meters above the home body's mean radius.
    private const double PeriapsisAltitudeM = 300_000.0;
    private const double FirstApoapsisAltitudeM = 1_000_000.0;
    private const double ApoapsisStepM = 1_000_000.0;
    private const int Samples = 8;
    private const double EccentricityTol = 1e-3;
    private const double RadiusTol = 1e-3; // relative

    public string Name => "example-sweep";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        if (session.System.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[example-sweep] FAIL: the loaded system has no home body.");
            return 1;
        }

        SimTime now = Universe.GetElapsedSimTime();
        double pe = home.MeanRadius + PeriapsisAltitudeM;
        HarnessData data = HarnessData.Create("example-sweep", "ap_altitude_m,dv_mps,eccentricity,sma_m,pass");
        int failures = 0;
        for (int i = 0; i < Samples; i++)
        {
            double apAltitude = FirstApoapsisAltitudeM + i * ApoapsisStepM;
            double ap = home.MeanRadius + apAltitude;
            Orbit orbit = VehicleSpawner.EllipticalCci(home, pe, ap, now);
            SimTime apoTime = orbit.GetNextApoapsisTime(now);
            double3 dv = OrbitalTransfers.DvCciToCircularize(orbit, apoTime);
            StateVectors sv = orbit.GetStateVectorsAt(apoTime);
            Orbit circular = Orbit.CreateFromStateCci(home, apoTime, sv.PositionCci, sv.VelocityCci + dv,
                VehicleSpawner.OrbitLineColor);

            bool ok = circular.Eccentricity < EccentricityTol && Math.Abs(circular.SemiMajorAxis - ap) / ap < RadiusTol;
            if (!ok)
                failures++;
            data.AppendRow(apAltitude, dv.Length(), circular.Eccentricity, circular.SemiMajorAxis, ok);
            HarnessLog.Line($"[example-sweep] '{home.Id}' apAlt={apAltitude:E3}m: dv={dv.Length():F3}m/s -> " +
                            $"ecc={circular.Eccentricity:F5} SMA={circular.SemiMajorAxis:E4} (target {ap:E4}) => {TestSupport.Verdict(ok)}");
        }

        HarnessLog.Line($"[example-sweep] {Samples} sample(s), {failures} failure(s) => {TestSupport.Verdict(failures == 0)}");
        return failures == 0 ? 0 : 1;
    }
}
