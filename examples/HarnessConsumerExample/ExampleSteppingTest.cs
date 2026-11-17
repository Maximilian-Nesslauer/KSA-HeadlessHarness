using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The worked example of the stepping helpers and the accessors: wait for a state with StepUntil
// instead of a hand-written loop with a guard, sample along a span with StepFor, and read game
// privates through TestSupport instead of reflection of your own. It coasts the flight-test vehicle
// in orbit, waits until the solver has put it into a physics bubble, then checks over five seconds
// that nothing touched it. Skips when the run names no vehicle save.
public sealed class ExampleSteppingTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;

    public string Name => "example-stepping";

    public int Run(HeadlessSession session)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrWhiteSpace(saveId))
        {
            HarnessLog.Line($"[example-stepping] SKIP: {TestSupport.VehicleEnvVar} is not set.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[example-stepping] SKIP: the loaded system has no home body.");
            return 0;
        }

        SimDriver driver = session.CreateDriver();
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        try
        {
            Vehicle vehicle;
            try
            {
                Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, driver.Elapsed);
                vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "ExampleStepping", orbit);
            }
            catch (InvalidOperationException e)
            {
                HarnessLog.Line($"[example-stepping] SKIP: {e.Message}");
                return 0;
            }

            // The throttle goes through the reference, so the other manual inputs stay as they are.
            ref ManualControlInputs inputs = ref TestSupport.GetManualControlInputs(vehicle);
            inputs.EngineThrottle = 0f;
            inputs.EngineOn = false;

            // Returns false and logs the message when the limit passes first.
            if (!driver.StepUntil(() => TestSupport.GetPhysicsBubble(vehicle) != null, 0.1, 10.0,
                    "the vehicle never joined a physics bubble"))
                return 1;

            int contacts = 0;
            driver.StepFor(5.0, 0.1, () =>
            {
                if (TestSupport.HadVehicleContact(vehicle))
                    contacts++;
            });

            bool ok = contacts == 0;
            HarnessLog.Line($"[example-stepping] coast of '{vehicle.Id}': {contacts} step(s) with a vehicle contact => {TestSupport.Verdict(ok)}");
            return ok ? 0 : 1;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }
}
