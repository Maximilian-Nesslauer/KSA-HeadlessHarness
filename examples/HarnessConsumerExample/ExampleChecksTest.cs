using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The worked example of a test that reports through HarnessChecks and reads its tunables with
// TestSupport.Param: every check is logged and counted, a case the loaded system cannot cover is a
// Skip, and Finish returns the exit code. The counts land in the run's results file. Run it with
// other values through the run script: -Param "example-checks.altitude=800000;example-checks.report=true".
public sealed class ExampleChecksTest : IHarnessTest
{
    private static readonly byte4 OrbitColor = new byte4(255, 255, 255, 255);

    public string Name => "example-checks";

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        double altitude = TestSupport.Param("example-checks.altitude", 400_000.0);
        int samples = TestSupport.Param("example-checks.samples", 4);
        bool writeReport = TestSupport.Param("example-checks.report", false);

        if (session.System.HomeBody is not IParentBody home)
        {
            t.Skip("the loaded system has no home body");
            return t.Finish();
        }

        double radius = home.MeanRadius + altitude;
        UniverseTime now = Universe.GetElapsedTime();
        Orbit orbit = VehicleSpawner.CircularCci(home, radius, now);
        t.CheckRel("circular orbit radius", orbit.SemiMajorAxis, radius, 1e-9);
        t.CheckAbs("circular orbit eccentricity", orbit.Eccentricity, 0.0, 1e-9);

        // A circular orbit keeps its radius at any time, so each sample checks the propagation.
        HarnessData? report = writeReport ? HarnessData.CreateText(Name, "txt") : null;
        double period = 2.0 * Math.PI * Math.Sqrt(radius * radius * radius / home.Mu);
        for (int i = 1; i <= samples; i++)
        {
            UniverseTime at = now + period * i / samples;
            double r = orbit.GetStateVectorsAt(at).PositionCci.Length();
            t.CheckRel($"radius after {i}/{samples} of a period", r, radius, 1e-6);
            report?.AppendLine($"sample {i}: t={(at - now).Seconds():F1} s, r={r:F1} m");
        }
        t.Info($"orbit around '{home.Id}' at {altitude:F0} m, period {period:F1} s");
        return t.Finish();
    }
}
