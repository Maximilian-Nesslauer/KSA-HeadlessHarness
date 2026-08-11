using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Spawns a controllable test vehicle into the loaded system in an arbitrary orbit, either from a
// player-built save in the game's Vehicles folder or as a copy of a live vehicle. Copies always go
// through serialize -> PartTree.Deserialize into a FRESH part tree, so the spawned vehicle shares no
// Part instances with any source - the public Vehicle.CreateVehicle(..., Part root, ...) overload
// otherwise does Parts = root.Tree, which would alias a live vehicle's tree.
public static class VehicleSpawner
{
    // Cosmetic orbit-line color required by Orbit.CreateFromStateCci. The harness never renders, so the
    // value is a placeholder; shared so the orbit helpers and tests do not each carry their own copy.
    public static readonly byte4 OrbitLineColor = new byte4(255, 255, 255, 255);

    // Spawns a vehicle from a save in the game's Vehicles folder (the saves VehicleSaves.
    // OnApplicationStart indexed during bring-up), the same way VehicleTemplate.CreateInto builds a
    // vehicle from a default save: deserialize the part tree, then restore the staged state
    // (SetActiveSequence), per-sequence performance environments, and fuel links. Engine active flags
    // round-trip through the part tree itself (EngineController.ApplySaveData), so a properly staged
    // save spawns with the correct engines already active. The vehicle is registered into the
    // parent's child list; Universe.ExecuteNextVehicleSolvers puts it in a PhysicsBubble on the next
    // solver step, joining an overlapping vehicle's bubble or renting a fresh one.
    public static Vehicle SpawnFromSave(string saveId, CelestialSystem system, IParentBody parent, string id, Orbit orbit)
    {
        VehicleSave? save = null;
        foreach (VehicleSave candidate in VehicleSaves.AsSpan())
        {
            if (string.Equals(candidate.Id, saveId, StringComparison.Ordinal))
            {
                save = candidate;
                break;
            }
        }
        if (save == null)
            throw new InvalidOperationException(
                $"vehicle save '{saveId}' not found in the game's Vehicles folder ({VehicleSaves.SaveFolderPath}).");

        PartInstance design = save.VehicleSaveData.RootPartInstance
            ?? throw new InvalidOperationException($"vehicle save '{saveId}' has no root part instance.");
        PartTree tree = PartTree.Deserialize(design);
        Vehicle vehicle = Vehicle.CreateVehicle(system, doubleQuat.Identity, double3.Zero, parent, id, tree.Root, orbit);
        vehicle.Parts.SequenceList.SetActiveSequence(save.VehicleSaveData.ActiveSequence);
        vehicle.Parts.SequenceList.ApplyEnvironments(save.VehicleSaveData.SequenceEnvironments);
        vehicle.Parts.FuelLinks.ApplySaveData(save.VehicleSaveData.FuelLinks, design);
        parent.Children.Add(vehicle);
        SeatRandomCrew(vehicle);
        return vehicle;
    }

    // Fills every free seat from the universe roster, mirroring Universe.AssignStartingCrew: mark the
    // vehicle launched, then take an unassigned, non-KIA kitten, seat it and start its mission.
    // Universe.LoadSystem seeds the roster with 20 kittens, so EnsureAvailableKitten only tops it up
    // once a run has seated all of them. Which kitten lands in which seat does not affect vehicle
    // mass or the flight test's determinism signature.
    //
    // Seats the save already assigned are left alone. Their hashes name kittens of the save's own
    // roster, which this run does not have, so the count below reports fewer crewed seats than the
    // vehicle has rather than silently reseating an occupied seat.
    public static int SeatRandomCrew(Vehicle vehicle)
    {
        int seats = vehicle.SeatCount;
        if (seats <= 0)
            return 0;

        vehicle.MarkLaunched();
        int seated = 0;
        for (int i = 0; i < seats; i++)
        {
            Universe.KittenRoster.EnsureAvailableKitten();
            KittenRosterEntryData? free = FindUnassignedKitten();
            if (free == null)
                break;
            if (!vehicle.AddCrewToFirstAvailableSeat(free.NameHash))
                break;
            free.StartMission(vehicle.Id);
            seated++;
        }

        HarnessLog.Line($"[harness] '{vehicle.Id}' crewed {seated}/{seats} seat(s) from the kitten roster.");
        return seated;
    }

    private static KittenRosterEntryData? FindUnassignedKitten()
    {
        foreach (KittenRosterEntryData kitten in Universe.KittenRoster.Kittens)
        {
            if (!kitten.AssignedToVehicle && !kitten.Kia)
                return kitten;
        }
        return null;
    }

    // Registers the copy the same way the game registers a decoupled stage: into the parent's child
    // list (so the solvers discover and tick it) and into the source's physics bubble (so it ticks
    // immediately and satisfies Vehicle.Split's PhysicsBubble requirement). Vehicle.CreateVehicle
    // covers only the CelestialSystem registration, which Astronomical's constructor does; without
    // these two steps the copy would neither tick nor be splittable.
    public static Vehicle SpawnCopy(Vehicle source, IParentBody parent, string id, Orbit orbit)
    {
        PartInstance design = source.SerializeSave().RootPartInstance
            ?? throw new InvalidOperationException($"source vehicle '{source.Id}' has no part instance to copy.");
        PartTree freshTree = PartTree.Deserialize(design);
        Vehicle copy = Vehicle.CreateVehicle(source.System, source.Body2Cce, source.BodyRates, parent, id, freshTree.Root, orbit);
        parent.Children.Add(copy);
        if (source.PhysicsBubble != null)
            copy.AddToBubble(source.PhysicsBubble);
        return copy;
    }

    // Tears a spawned vehicle back out of the simulation: off its physics bubble and out of the
    // celestial tree plus the parent's child list (CelestialSystem.Deregister drops both). The
    // counterpart to the spawn helpers - call it on every spawned vehicle and every stage it shed, so
    // throwaway vehicles stop ticking. A bubble left with no vehicles is recycled by the game on the
    // next solver step (Universe.TrimPhysicsBubbles).
    public static void Despawn(Vehicle vehicle)
    {
        PhysicsBubble? bubble = vehicle.PhysicsBubble;
        if (bubble != null)
            vehicle.RemoveFromBubble(bubble);
        vehicle.System.Deregister(vehicle);
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
