using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Compares AeroProbe with the solver: Test Vehicle 1 flies off-rails through the upper atmosphere of
// the home body with its engines off, and after each short step the acceleration the solver measured
// from the non-gravitational forces (Vehicle.AccelerationBody) must match the probe's force over the
// vehicle mass at the same flow state. The probe is evaluated before and after each step and averaged,
// because the solver integrates over the step. Buoyancy, the drag saturation and the one degree flow
// cache of the live AeroSurfaceFlow are left out of the probe, which the tolerance covers at this
// dynamic pressure. Opt-in, because it spawns into the atmosphere and steps at a small dt.
public sealed class AeroProbeVsFlightTest : IHarnessTest
{
    private const double AltitudeM = 45_000.0;
    private const double InertialSpeed = 1_600.0;  // m/s along CCI +Y at CCI +X, about 1.1 km/s over the surface
    private const double StepSeconds = 0.1;
    private const int SettleSteps = 2;
    private const int CompareSteps = 10;
    private const double Tolerance = 0.05;          // relative to the probe acceleration
    private const double MinAcceleration = 0.05;    // m/s^2; below it the comparison means nothing

    public string Name => "aero-probe-vs-flight";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        if (!PropulsionTestVehicles.SaveExists())
        {
            HarnessLog.Line($"[aero-probe-vs-flight] SKIP: no '{PropulsionTestVehicles.SaveId}' save in {VehicleSaves.SaveFolderPath}.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home || home.GetPhysicalAtmosphere() is not PhysicalAtmosphere atmosphere
            || !(AltitudeM < atmosphere.Height))
        {
            HarnessLog.Line($"[aero-probe-vs-flight] SKIP: the home body has no atmosphere at {AltitudeM / 1000:F0}km.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool forceOffRails = PhysicsBubble._forceOffRails;
        bool ok = true;
        int compared = 0;
        try
        {
            UniverseTime now = Universe.GetElapsedTime();
            double radius = home.MeanRadius + AltitudeM;
            Orbit orbit = Orbit.CreateFromStateCci(home, now, new double3(radius, 0.0, 0.0), new double3(0.0, InertialSpeed, 0.0),
                VehicleSpawner.OrbitLineColor);
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(PropulsionTestVehicles.SaveId, system, home, "HarnessAeroProbeVsFlight", orbit);
            PhysicsBubble._forceOffRails = true;
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
            SimDriver driver = session.CreateDriver();
            driver.Step(StepSeconds, SettleSteps);

            for (int i = 0; i < CompareSteps; i++)
            {
                AeroLoads before = AeroProbe.MeasureCurrent(vehicle);
                driver.Step(StepSeconds);
                AeroLoads after = AeroProbe.MeasureCurrent(vehicle);
                double mass = vehicle.TotalMass;
                double3 expected = 0.5 * (before.ForceBody + after.ForceBody) / mass;
                double3 measured = vehicle.AccelerationBody;
                double error = (measured - expected).Length();
                if (expected.Length() < MinAcceleration)
                {
                    HarnessLog.Line($"[aero-probe-vs-flight] step {i}: probe acceleration {expected.Length():G3}m/s^2 is too small to compare.");
                    continue;
                }
                compared++;
                bool stepOk = error <= Tolerance * expected.Length();
                HarnessLog.Line($"[aero-probe-vs-flight] step {i}: alt {after.Atmosphere.Altitude / 1000:F2}km Mach {after.Mach:F2} q={after.DynamicPressure:F0}Pa " +
                                $"measured {Format(measured)} probe {Format(expected)} m/s^2, error {error / expected.Length():P2} => {TestSupport.Verdict(stepOk)}");
                ok &= stepOk;
            }
        }
        finally
        {
            PhysicsBubble._forceOffRails = forceOffRails;
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        if (compared == 0)
        {
            HarnessLog.Line("[aero-probe-vs-flight] FAIL: no step had an aerodynamic acceleration large enough to compare.");
            ok = false;
        }
        HarnessLog.Line($"[aero-probe-vs-flight] {TestSupport.Verdict(ok)} ({compared} step(s) compared)");
        return ok ? 0 : 1;
    }

    private static string Format(double3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}
