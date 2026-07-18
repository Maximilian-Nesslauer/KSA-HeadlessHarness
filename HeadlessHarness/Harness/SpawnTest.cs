using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Verifies safe vehicle copying: clone the first live vehicle in the system into a FRESH part tree
// on a chosen circular orbit above its own and confirm the copy reports that orbit. Skips when the
// loaded system ships no vehicle (nothing generic to copy).
public sealed class SpawnTest : IHarnessTest
{
    private const double AltitudeOffsetM = 500_000.0; // spawn the copy clear of the source orbit
    private const double SmaTol = 1e-3;               // relative
    private const double EccentricityTol = 1e-3;

    public string Name => "spawn";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        Vehicle? source = null;
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle v)
            {
                source = v;
                break;
            }
        }
        if (source == null)
        {
            HarnessLog.Line("[spawn] SKIP: the loaded system has no vehicle to copy.");
            return 0;
        }

        IParentBody parent = source.Orbit.Parent;
        double radius = source.Orbit.SemiMajorAxis + AltitudeOffsetM;
        SimTime now = Universe.GetElapsedSimTime();
        Orbit target = VehicleSpawner.CircularCci(parent, radius, now);
        Vehicle spawned = VehicleSpawner.SpawnCopy(source, parent, "HarnessTestSat", target);
        try
        {
            Orbit o = spawned.Orbit;
            bool smaOk = Math.Abs(o.SemiMajorAxis - radius) / radius < SmaTol;
            bool eccOk = o.Eccentricity < EccentricityTol;
            bool ok = smaOk && eccOk;
            HarnessLog.Line($"[spawn] '{spawned.Id}' (copy of '{source.Id}') mass={spawned.TotalMass:F0}kg " +
                            $"SMA={o.SemiMajorAxis:E4} (target {radius:E4}) ecc={o.Eccentricity:F5} => {(ok ? "PASS" : "FAIL")}");
            return ok ? 0 : 1;
        }
        finally
        {
            VehicleSpawner.Despawn(spawned);
        }
    }
}
