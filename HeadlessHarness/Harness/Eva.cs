using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Kitten EVA through the game's own paths. Headless the kitten has no avatar, because
// HeadlessSession skips the KittenRenderable constructor and its Dispose, so it spawns, flies,
// boards and despawns as a plain vehicle.
//
// Both the EVA spawn (EVADoor.SpawnEva) and boarding (KittenEva.BoardVehicle) make the player
// control the new vehicle: they set Program.ControlledVehicle and point the hovered camera at it.
// The helpers here put Program.ControlledVehicle back afterwards, so a test leaves it as it found
// it. The camera follow stays, and Universe.DestroyVehicle hands it off when that vehicle goes.
public static class Eva
{
    // The vehicle id EVADoor.CreateKittenEva assigns the kitten's roster entry while it is out.
    public const string EvaVehicleId = "EVA";

    // The first seat that holds a kitten of the run's roster, or null.
    public static IVASeat? FindCrewedSeat(Vehicle vehicle)
    {
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash != KeyHash.Zero && Universe.KittenRoster.Find(seat.AssignedKittenHash) != null)
                return seat;
        }
        return null;
    }

    // Queues the EVA of the seat's kitten through the door aligned with the seat, or the first
    // door (EVADoor.PerformEvaForSeat). The kitten appears in the input drain of the next
    // SimDriver.Step, the same one-frame latency as the EVA button in the game.
    public static EvaRequest Request(Vehicle vehicle, IVASeat seat) => EVADoor.PerformEvaForSeat(vehicle, seat);

    // Requests the EVA, steps once and returns the kitten. EVADoor.SpawnEva refuses quietly when
    // the kitten cannot go out (Vehicle.CheckEva, which with life support on checks its condition
    // and the suit supplies), which surfaces here as an exception.
    public static KittenEva SpawnEva(SimDriver driver, Vehicle vehicle, IVASeat seat, double dt = 0.1)
    {
        KittenRosterEntryData kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash)
            ?? throw new InvalidOperationException($"the seat of '{vehicle.Id}' holds no kitten of the run's roster.");
        string kittenId = kitten.Name;
        Vehicle? controlled = Program.ControlledVehicle;
        try
        {
            EvaRequest request = Request(vehicle, seat);
            if (request != EvaRequest.Queued)
                throw new InvalidOperationException($"'{vehicle.Id}' refused the EVA of '{kittenId}': {request}.");
            driver.Step(dt);
        }
        finally
        {
            RestoreControl(controlled);
        }

        if (vehicle.System.All.Get(kittenId) is not KittenEva eva)
            throw new InvalidOperationException(
                $"'{kittenId}' did not leave '{vehicle.Id}' (EVADoor.SpawnEva refused, see Vehicle.CheckEva).");
        HarnessLog.Line($"[harness] '{kittenId}' is on EVA from '{vehicle.Id}'.");
        return eva;
    }

    // Boards the kitten into the host through KittenEva.BoardVehicle, which seats it, destroys the
    // EVA vehicle and, with life support on, hands the suit supplies back. The door is the one
    // KittenEva.FindBoardTarget picks, as the board key does, when that is a door of this host
    // within its 1 m reach. Otherwise it is the first door of the host whose seat check passes
    // (KittenEva.CheckBoarding Ready or OutOfRange), so a test can board without flying the kitten
    // to the door first.
    public static CrewSeatResult Board(KittenEva kitten, Vehicle host)
    {
        EVADoor? door = null;
        BoardTarget target = kitten.FindBoardTarget();
        bool inReach = target.Status == BoardTargetStatus.Found && ReferenceEquals(target.Host, host);
        if (inReach)
        {
            door = target.Door;
        }
        else
        {
            foreach (EVADoor candidate in host.Parts.EVADoors.Modules)
            {
                BoardingCheck check = kitten.CheckBoarding(host, candidate);
                if (check is BoardingCheck.Ready or BoardingCheck.OutOfRange)
                {
                    door = candidate;
                    break;
                }
            }
        }
        if (door == null)
            throw new InvalidOperationException($"'{host.Id}' has no door with a free seat for '{kitten.Id}'.");

        string kittenId = kitten.Id;
        Vehicle? controlled = Program.ControlledVehicle;
        CrewSeatResult result;
        try
        {
            result = kitten.BoardVehicle(host, door);
        }
        finally
        {
            RestoreControl(ReferenceEquals(controlled, kitten) ? null : controlled);
        }
        HarnessLog.Line($"[harness] '{kittenId}' boarding '{host.Id}' ({(inReach ? "door in reach" : "door out of reach")}): {result}.");
        return result;
    }

    private static void RestoreControl(Vehicle? controlled)
    {
        Program.ControlledVehicle = controlled is { IsDisposed: false } ? controlled : null;
    }
}
