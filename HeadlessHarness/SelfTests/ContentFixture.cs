using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// The vehicle the content, save and step statistics self-tests work on: the save KSA_HEADLESS_VEHICLE
// names, on a circular orbit 500 km above the home body's mean radius, flown for a few seconds so its
// engines, tanks and flight computer hold flight state rather than the state of the editor.
internal static class ContentFixture
{
    private const double SpawnAltitudeM = 500_000.0;

    // Null with a SKIP or FAIL line logged when there is nothing to fly. A spawn that throws after
    // registration still leaves the vehicle in the system, so callers spawn inside their cleanup scope.
    public static Vehicle? Spawn(HeadlessSession session, string tag, string id, out int failure)
    {
        failure = 0;
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"[{tag}] SKIP: {TestSupport.VehicleEnvVar} not set (name a save in the game's Vehicles folder to run this test).");
            return null;
        }
        if (session.System.HomeBody is not IParentBody home)
        {
            HarnessLog.Line($"[{tag}] SKIP: the loaded system has no home body to orbit.");
            return null;
        }
        Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, Universe.GetElapsedTime());
        try
        {
            return VehicleSpawner.SpawnFromSave(saveId, session.System, home, id, orbit);
        }
        catch (InvalidOperationException e)
        {
            HarnessLog.Line($"[{tag}] FAIL: {e.Message}");
            failure = 1;
            return null;
        }
    }

    // A short burn of the active stage (the first sequence is activated when nothing fires yet), then
    // a short coast with the engines off. Needs PhysicsBubble._forceOffRails set by the caller.
    public static void Fly(Vehicle vehicle, SimDriver driver, int burnSeconds, int coastSeconds)
    {
        vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
        TestSupport.SetManualControlInputs(vehicle, 1f, engineOn: true);
        if (!TestSupport.AnyActiveEngineFed(vehicle) && vehicle.Parts.SequenceList.GetNextSequenceNumber() != -1)
            vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
        driver.Step(1.0, burnSeconds);
        TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
        driver.Step(1.0, coastSeconds);
    }
}
