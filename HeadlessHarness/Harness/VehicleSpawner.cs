using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Spawns a controllable test vehicle into the loaded system, from a vehicle save (bundled with the
// harness or a consumer, or in the game's Vehicles folder), from a part tree built in code, or as a
// copy of a live vehicle. Copies always go through serialize -> PartTree.Deserialize into a FRESH
// part tree, so the spawned vehicle shares no Part instances with any source - the public
// Vehicle.CreateVehicle(..., Part root, ...) overload otherwise does Parts = root.Tree, which would
// alias a live vehicle's tree.
public static partial class VehicleSpawner
{
    // Cosmetic orbit-line color required by Orbit.CreateFromStateCci. The harness never renders, so the
    // value is a placeholder; shared so the orbit helpers and tests do not each carry their own copy.
    public static readonly byte4 OrbitLineColor = new byte4(255, 255, 255, 255);

    // Spawns a vehicle save by name (ResolveVehicleSave: the bundled vehicles first, then the
    // game's Vehicles folder) through SpawnFromSaveData, with crew and without filling tanks.
    public static Vehicle SpawnFromSave(string saveId, CelestialSystem system, IParentBody parent, string id, Orbit orbit)
        => SpawnFromSave(saveId, system, parent, id, orbit, SpawnOptions.Default);

    // applyTankAssignments is SpawnOptions.FillTanks. Without it the restore matches
    // VehicleTemplate.CreateInto, which does not fill tanks.
    public static Vehicle SpawnFromSave(string saveId, CelestialSystem system, IParentBody parent, string id, Orbit orbit, bool applyTankAssignments)
        => SpawnFromSave(saveId, system, parent, id, orbit, SpawnOptions.Default with { FillTanks = applyTankAssignments });

    // Fills every free seat from the universe roster, mirroring Universe.AssignStartingCrew: mark the
    // vehicle launched, then take an unassigned kitten that is neither retired nor KIA. Seating it
    // through Vehicle.SetSeatCrew starts its mission.
    // Universe.LoadSystem seeds the roster with 20 kittens, so EnsureAvailableKitten only tops it up
    // once a run has seated all of them. Which kitten lands in which seat does not affect vehicle
    // mass or the flight test's determinism signature.
    //
    // A seat keeps its kitten only when that kitten is in this run's roster and assigned to this
    // vehicle. A save names kittens of its own roster, and a copy names the kittens of its source.
    // Such a seat counts as occupied but holds no crew, and Vehicle.ComputeControl gives a pod with
    // no crew no control (NoCrew), so the seat is cleared and filled again.
    public static int SeatRandomCrew(Vehicle vehicle)
    {
        int seats = vehicle.SeatCount;
        if (seats <= 0)
            return 0;

        int cleared = ClearForeignSeats(vehicle);
        if (cleared > 0)
            HarnessLog.Line($"[harness] '{vehicle.Id}' cleared {cleared} seat(s) that named a kitten not crewing this vehicle.");

        vehicle.MarkLaunched();
        int seated = 0;
        for (int i = 0; i < seats; i++)
        {
            Universe.KittenRoster.EnsureAvailableKitten();
            KittenRosterEntryData? free = FindUnassignedKitten();
            if (free == null)
                break;
            CrewSeatResult result = vehicle.AddCrewToFirstAvailableSeat(free.NameHash);
            if (result != CrewSeatResult.Seated)
            {
                HarnessLog.Line($"[harness] '{vehicle.Id}' seat {i}: {result}, stop seating.");
                break;
            }
            seated++;
        }

        HarnessLog.Line($"[harness] '{vehicle.Id}' crewed {seated}/{seats} seat(s) from the kitten roster.");
        return seated;
    }

    private static int ClearForeignSeats(Vehicle vehicle)
    {
        int cleared = 0;
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash == KeyHash.Zero)
                continue;
            KittenRosterEntryData? kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash);
            if (kitten != null && string.Equals(kitten.AssignedVehicleId, vehicle.Id, StringComparison.Ordinal))
                continue;
            seat.AssignedKittenHash = KeyHash.Zero;
            cleared++;
        }
        return cleared;
    }

    private static KittenRosterEntryData? FindUnassignedKitten()
    {
        foreach (KittenRosterEntryData kitten in Universe.KittenRoster.Kittens)
        {
            if (!kitten.AssignedToVehicle && !kitten.Retired && !kitten.Kia && !kitten.HasRadiationSickness(Universe.GetElapsedTime()))
                return kitten;
        }
        return null;
    }

    private static PhysicsBubble? FindPhysicsBubble(Vehicle vehicle)
        => TestSupport.GetPhysicsBubble(vehicle);

    // Registers the copy the same way the game registers a decoupled stage: into the parent's child
    // list (so the solvers discover and tick it) and into the source's physics bubble (so it ticks
    // immediately and satisfies Vehicle.Split's PhysicsBubble requirement). Vehicle.CreateVehicle
    // covers only the CelestialSystem registration, which Astronomical's constructor does; without
    // these two steps the copy would neither tick nor be splittable.
    //
    // The copy takes the source's mission, as Vehicle.Split gives a stage its parent's, and crew of
    // its own: the serialized seats name the source's kittens, and one kitten in two vehicles would
    // be credited twice and leave both when either is despawned.
    public static Vehicle SpawnCopy(Vehicle source, IParentBody parent, string id, Orbit orbit)
    {
        PartInstance design = source.SerializeSave().RootPartInstance
            ?? throw new InvalidOperationException($"source vehicle '{source.Id}' has no part instance to copy.");
        EnsureIdAvailable(source.System, id);
        PartTree freshTree = PartTree.Deserialize(design);
        freshTree.Mission = source.Parts.Mission?.Clone();
        Vehicle? copy = null;
        try
        {
            copy = Vehicle.CreateVehicle(source.System, source.Body2Cce, source.BodyRates, parent, id, freshTree.Root, orbit);
            parent.Children.Add(copy);
            if (FindPhysicsBubble(source) is PhysicsBubble bubble)
                copy.AddToBubble(bubble);
            SeatRandomCrew(copy);
            return copy;
        }
        catch
        {
            RollBackSpawn(source.System, id, copy, freshTree);
            throw;
        }
    }

    // Tears a spawned vehicle back out through the game's own destroy path: settles the crew, hands
    // off any camera following it, clears any target aimed at it, drops it from its bubble and the
    // celestial tree, and disposes its update state and part tree. Call it on every spawned vehicle,
    // every stage it shed and every piece of debris a breakup left. Doing this by hand instead leaks
    // the update state, the part tree and the audio registration. Idempotent.
    //
    // EndMission is spelled out so a change to the parameter default cannot silently start killing
    // this run's crew: Kill marks the kittens KIA for the rest of the process, starving later
    // spawns of seatable crew.
    //
    // A Kitten EVA holds no seat, so Vehicle.EndAllCrewMissions does not release its kitten, which
    // EVADoor.CreateKittenEva assigned to the vehicle id "EVA" (KittenRosterEntryData.TransferToVehicle).
    // Its mission is ended here, so the kitten can crew a later spawn again.
    public static void Despawn(Vehicle vehicle)
    {
        if (vehicle is KittenEva kitten && !kitten.IsDisposed)
        {
            KittenRosterEntryData? entry = Universe.KittenRoster.Find(kitten.Id);
            if (entry != null && string.Equals(entry.AssignedVehicleId, Eva.EvaVehicleId, StringComparison.Ordinal))
                entry.EndMission();
        }
        Universe.DestroyVehicle(vehicle, CrewDisposition.EndMission);
    }

    // A circular orbit of the given radius (meters from the parent centre), velocity along CCI +Y.
    public static Orbit CircularCci(IParentBody parent, double radius, UniverseTime time)
    {
        double v = Math.Sqrt(parent.Mu / radius);
        return Orbit.CreateFromStateCci(parent, time, new double3(radius, 0.0, 0.0), new double3(0.0, v, 0.0), OrbitLineColor);
    }

    // An elliptical orbit with the given periapsis/apoapsis radii, starting at periapsis (CCI +X).
    public static Orbit EllipticalCci(IParentBody parent, double periapsisRadius, double apoapsisRadius, UniverseTime time)
    {
        double a = (periapsisRadius + apoapsisRadius) / 2.0;
        double vPe = Math.Sqrt(parent.Mu * (2.0 / periapsisRadius - 1.0 / a));
        return Orbit.CreateFromStateCci(parent, time, new double3(periapsisRadius, 0.0, 0.0), new double3(0.0, vPe, 0.0), OrbitLineColor);
    }
}
