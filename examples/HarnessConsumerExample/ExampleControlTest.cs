using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The setup a consumer test copies when it flies a vehicle on Auto modes: pin the game rules for
// the test, seed the radiation upsets so the run repeats, make sure the vehicle has crew and
// power, and let the driver stop on the game's own step limits. Everything is restored when the
// scopes end, and the spawned vehicle is torn down in the finally block.
public sealed class ExampleControlTest : IHarnessTest
{
    private const double AltitudeM = 500_000.0;
    private const double FlightS = 120.0;
    private const double StepS = 10.0;

    public string Name => "example-control";

    public int Run(HeadlessSession session)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"[{Name}] SKIP: {TestSupport.VehicleEnvVar} not set.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line($"[{Name}] SKIP: the loaded system has no home body.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        try
        {
            using IDisposable rules = TestSupport.UseRules(radiation: true, lifeSupport: false);
            using IDisposable seed = TestSupport.SeedUpsets(42);

            Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + AltitudeM, Universe.GetElapsedTime());
            Vehicle vehicle;
            try
            {
                vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "ExampleControl", orbit);
            }
            catch (InvalidOperationException e)
            {
                HarnessLog.Line($"[{Name}] FAIL: {e.Message}");
                return 1;
            }
            ControlExpectation control = TestSupport.EnsureControllable(vehicle);
            HarnessLog.Line($"[{Name}] '{saveId}': {control.Reason} -> {control.Predicted}.");
            if (!control.Expected)
            {
                HarnessLog.Line($"[{Name}] SKIP: '{saveId}' cannot be controlled ({control.Reason}).");
                return 0;
            }

            vehicle.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;
            driver.ClipToGameStepLimit = true;
            UniverseTime end = driver.Elapsed + FlightS;
            while (driver.Elapsed < end)
                driver.Step(StepS);

            bool ok = vehicle.IsControllable && vehicle.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto;
            HarnessLog.Line($"[{Name}] after {FlightS:F0} s: control {TestSupport.GetControl(vehicle)}, " +
                            $"attitude {vehicle.FlightComputer.AttitudeMode} => {TestSupport.Verdict(ok)}");
            return ok ? 0 : 1;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
            TestSupport.ClearAlerts();
            TestSupport.ResetSimulationSpeed();
        }
    }
}
