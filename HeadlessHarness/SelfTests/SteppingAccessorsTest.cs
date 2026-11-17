using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Binds every TestSupport accessor of a game private and reads it on the flight-test vehicle in
// orbit: the manual inputs by reference, the update state, the bubble, the contact flags, and the
// two resource manager calls. A rename in the game fails here with the drift message before a
// consumer test meets it. Skips when the run names no vehicle save.
public sealed class SteppingAccessorsTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;

    public string Name => "accessors";

    public int Run(HeadlessSession session)
    {
        string? saveId = SteppingSelfTestSupport.ResolveFlightSave(Name);
        if (saveId == null)
            return 0;
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[accessors] SKIP: the loaded system has no home body.");
            return 0;
        }

        SimDriver driver = session.CreateDriver();
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool ok = true;
        try
        {
            Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, driver.Elapsed);
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "HarnessAccessorsTest", orbit);

            ok &= Check("update state", TestSupport.GetUpdateState(vehicle) is { } state && ReferenceEquals(state.ReadOnlyVehicle, vehicle),
                "bound to the vehicle");

            // A write through the reference is what the next read sees, and SetManualControlInputs
            // writes the same field.
            ref ManualControlInputs inputs = ref TestSupport.GetManualControlInputs(vehicle);
            inputs.EngineThrottle = 0.25f;
            inputs.EngineOn = false;
            float throttleByRef = TestSupport.GetManualControlInputs(vehicle).EngineThrottle;
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
            float throttleAfterSet = TestSupport.GetManualControlInputs(vehicle).EngineThrottle;
            ok &= Check("manual inputs by reference", throttleByRef == 0.25f && throttleAfterSet == 0f,
                $"throttle {throttleByRef} by reference, {throttleAfterSet} after SetManualControlInputs");

            driver.Step(1.0);

            PhysicsBubble? bubble = TestSupport.GetPhysicsBubble(vehicle);
            ok &= Check("physics bubble", (bubble != null) == vehicle.HasPhysicsBubble,
                $"bubble {(bubble != null ? "present" : "absent")}, Vehicle.HasPhysicsBubble {vehicle.HasPhysicsBubble}");
            ok &= Check("no vehicle contact in orbit", !TestSupport.HadVehicleContact(vehicle), "");
            ok &= Check("no static collider in orbit", !TestSupport.HasStaticCollider(vehicle), "");

            float propellant = vehicle.PropellantMass;
            TestSupport.RecreateResourceManagers(vehicle.Parts);
            TestSupport.FlushDirtyResourceManagers();
            driver.Step(1.0);
            // A coasting step can boil off a little cryogenic propellant, nothing more.
            bool kept = propellant > 0f
                ? Math.Abs(vehicle.PropellantMass - propellant) <= 1e-3 * propellant
                : vehicle.PropellantMass == propellant;
            ok &= Check("resource managers rebuilt", kept,
                $"propellant {propellant:F3}kg before, {vehicle.PropellantMass:F3}kg after the rebuild and a coasting step");
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        HarnessLog.Line($"[accessors] => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool Check(string what, bool pass, string detail)
    {
        HarnessLog.Line($"[accessors] {what}{(detail.Length > 0 ? ": " + detail : "")} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
