using Brutal.Numerics;
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
    // parent's child list; an update task is assigned by the game itself on the next solver step
    // (Universe.ExecuteNextVehicleSolvers -> AddVehiclesToTasks).
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
        return vehicle;
    }

    // Registers the copy the same way the game registers a decoupled stage: into the parent's child
    // list (so the solvers discover and tick it) and into the source's update task (so it ticks
    // immediately and satisfies Vehicle.Split's UpdateTask requirement). Vehicle.CreateVehicle alone
    // leaves the vehicle unregistered - it would neither tick nor be splittable.
    public static Vehicle SpawnCopy(Vehicle source, IParentBody parent, string id, Orbit orbit)
    {
        PartInstance design = source.SerializeSave().RootPartInstance
            ?? throw new InvalidOperationException($"source vehicle '{source.Id}' has no part instance to copy.");
        PartTree freshTree = PartTree.Deserialize(design);
        Vehicle copy = Vehicle.CreateVehicle(source.System, source.Body2Cce, source.BodyRates, parent, id, freshTree.Root, orbit);
        parent.Children.Add(copy);
        if (source.UpdateTask != null)
            copy.AddToTask(source.UpdateTask);
        return copy;
    }

    // Tears a spawned vehicle back out of the simulation: off its update task and out of the celestial
    // tree plus the parent's child list (CelestialSystem.Deregister drops both). The counterpart to
    // the spawn helpers - call it on every spawned vehicle and every stage it shed, so throwaway
    // vehicles stop ticking.
    public static void Despawn(Vehicle vehicle)
    {
        if (vehicle.UpdateTask != null)
            vehicle.RemoveFromTask(vehicle.UpdateTask);
        vehicle.System.Deregister(vehicle);
    }

    // A circular orbit of the given radius (meters from the parent centre), velocity along CCI +Y.
    public static Orbit CircularCci(IParentBody parent, double radius, SimTime time)
    {
        double v = Math.Sqrt(parent.Mu / radius);
        return Orbit.CreateFromStateCci(parent, time, new double3(radius, 0.0, 0.0), new double3(0.0, v, 0.0), OrbitLineColor);
    }

    // An elliptical orbit with the given periapsis/apoapsis radii, starting at periapsis (CCI +X).
    public static Orbit EllipticalCci(IParentBody parent, double periapsisRadius, double apoapsisRadius, SimTime time)
    {
        double a = (periapsisRadius + apoapsisRadius) / 2.0;
        double vPe = Math.Sqrt(parent.Mu * (2.0 / periapsisRadius - 1.0 / a));
        return Orbit.CreateFromStateCci(parent, time, new double3(periapsisRadius, 0.0, 0.0), new double3(0.0, vPe, 0.0), OrbitLineColor);
    }
}
