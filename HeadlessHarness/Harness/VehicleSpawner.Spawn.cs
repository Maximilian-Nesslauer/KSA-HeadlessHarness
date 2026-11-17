using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// How SpawnTree, SpawnFromSaveData and the surface spawns set up a vehicle.
public sealed record SpawnOptions
{
    public static readonly SpawnOptions Default = new SpawnOptions();

    // Fills the free seats from the run's kitten roster (VehicleSpawner.SeatRandomCrew). False
    // leaves every seat empty: a seat that names a kitten not crewing this vehicle is cleared all
    // the same, because such a seat counts as occupied but holds no crew.
    public bool SeatCrew { get; init; } = true;

    // Runs PartTree.ApplyTankAssignments, as VehicleLaunchMenu.LoadPreview does before a player
    // launch, so a tank without contents fills with its automatic mix. A tank that already holds
    // its mix keeps its contents.
    public bool FillTanks { get; init; }

    // Body attitude and body rates (rad/s in the body frame) of a spawn in orbit. Null is the
    // identity attitude. A surface spawn takes both from the location and ignores these two.
    public doubleQuat? Body2Cce { get; init; }

    public double3 BodyRates { get; init; }

    // True picks a free id (id, id_1, id_2, ...) when the requested one is taken, the way
    // VehicleLaunchMenu.LaunchVehicle renames a launch. False throws instead, so a test that
    // leaked a vehicle fails loudly.
    public bool MakeIdUnique { get; init; }
}

public static partial class VehicleSpawner
{
    // Spawns a part tree that belongs to no vehicle yet, such as one VehicleBuilder.Build made or
    // one PartTree.Deserialize made. The vehicle takes the tree itself (Vehicle.CreateVehicle sets
    // Parts = root.Tree), so a tree spawns once; deserialize or build a new one for each spawn.
    public static Vehicle SpawnTree(PartTree tree, CelestialSystem system, IParentBody parent, string id, Orbit orbit, SpawnOptions? options = null)
    {
        EnsureTreeFree(tree, id);
        options ??= SpawnOptions.Default;
        string vehicleId = PrepareId(system, id, options);
        return SpawnCore(tree, system, parent, vehicleId, orbit, null, options, null);
    }

    // Spawns a vehicle save the way VehicleTemplate.CreateInto builds a vehicle from a default
    // save: deserialize the part tree, then restore the staged state (SetActiveSequence), the
    // per-sequence performance environments, the fuel links and the mission. Engine active flags
    // round-trip through the part tree itself (EngineController.ApplySaveData), so a staged save
    // spawns with the correct engines already active.
    public static Vehicle SpawnFromSaveData(VehicleSaveData data, CelestialSystem system, IParentBody parent, string id, Orbit orbit, SpawnOptions? options = null)
    {
        options ??= SpawnOptions.Default;
        PartTree tree = DeserializeSave(data, out string vehicleId, system, id, options);
        return SpawnCore(tree, system, parent, vehicleId, orbit, null, options, data);
    }

    public static Vehicle SpawnFromSave(string saveId, CelestialSystem system, IParentBody parent, string id, Orbit orbit, SpawnOptions options)
    {
        VehicleSaveData data = ReadSave(saveId);
        return SpawnFromSaveData(data, system, parent, id, orbit, options);
    }

    // Throws when a vehicle of this id is registered already. LookupCollection.Register only logs
    // a duplicate and leaves the new vehicle out of the system, where nothing finds or despawns it.
    public static void EnsureIdAvailable(CelestialSystem system, string id)
    {
        if (system.All.Get(id) != null)
            throw new InvalidOperationException($"cannot spawn vehicle '{id}' because the id is already registered.");
    }

    // The id itself when it is free, otherwise the first free one of id_1, id_2, and so on.
    public static string UniqueVehicleId(CelestialSystem system, string id)
    {
        if (system.All.Get(id) == null)
            return id;
        for (int n = 1; ; n++)
        {
            string candidate = $"{id}_{n}";
            if (system.All.Get(candidate) == null)
                return candidate;
        }
    }

    // The checks the game makes before it builds a saved design. PartTree.Deserialize throws a bare
    // NullReferenceException on the first part id the game no longer loads, so the missing parts
    // are named first, as CelestialSystem.DeserializeSave and UncompressedVehicleSave.Load do. A
    // save made before a part lost connectors names connector indices the part no longer has, and
    // PartTree.Deserialize indexes past the end on them, so they are dropped the way
    // UncompressedVehicleSave.Load drops them. Returns the number of dropped connectors.
    public static int EnsurePartsLoaded(PartInstance design, string name)
    {
        string? missingParts = design.FindMissingParts();
        if (missingParts != null)
            throw new InvalidOperationException(
                $"vehicle save '{name}' uses parts that are missing or failed to load ({missingParts}). " +
                "Re-save it in the game, or build it again from the current parts.");
        int dropped = design.DropStaleConnectors();
        if (dropped > 0)
            HarnessLog.Line($"[harness] vehicle save '{name}': dropped {dropped} connector(s) its parts no longer have.");
        return dropped;
    }

    private static VehicleSaveData ReadSave(string saveId)
    {
        VehicleSaveSource source = ResolveVehicleSave(saveId);
        HarnessLog.Line($"[harness] vehicle save '{saveId}' from {source.Describe()}.");
        return source.ReadData();
    }

    private static PartTree DeserializeSave(VehicleSaveData data, out string vehicleId, CelestialSystem system, string id, SpawnOptions options)
    {
        string name = string.IsNullOrEmpty(data.Id) ? id : data.Id;
        PartInstance design = data.RootPartInstance
            ?? throw new InvalidOperationException($"vehicle save '{name}' has no root part instance.");
        EnsurePartsLoaded(design, name);
        vehicleId = PrepareId(system, id, options);
        return PartTree.Deserialize(design);
    }

    private static string PrepareId(CelestialSystem system, string id, SpawnOptions options)
    {
        if (options.MakeIdUnique)
            return UniqueVehicleId(system, id);
        EnsureIdAvailable(system, id);
        return id;
    }

    private static void EnsureTreeFree(PartTree tree, string id)
    {
        if (tree.IsDisposed)
            throw new InvalidOperationException($"cannot spawn '{id}' from a disposed part tree.");
        if (tree.OwningVehicle != null)
            throw new InvalidOperationException(
                $"cannot spawn '{id}' from the part tree of the live vehicle '{tree.OwningVehicle.Id}'; " +
                "use SpawnCopy, or deserialize a fresh tree.");
    }

    // Every spawn path ends here. The vehicle is registered into the parent's child list;
    // Universe.ExecuteNextVehicleSolvers puts it in a PhysicsBubble on the next solver step,
    // joining an overlapping vehicle's bubble or renting a fresh one.
    //
    // Vehicle.CreateVehicle registers the vehicle with the CelestialSystem inside Astronomical's
    // constructor, so anything that throws after that point leaves a live vehicle in the system
    // that the caller has no handle to. The rollback tears it down again before the rethrow.
    private static Vehicle SpawnCore(PartTree tree, CelestialSystem system, IParentBody parent, string id, Orbit? orbit,
        SurfaceRequest? surface, SpawnOptions options, VehicleSaveData? data)
    {
        Vehicle? vehicle = null;
        try
        {
            if (options.FillTanks)
                tree.ApplyTankAssignments();

            doubleQuat body2Cce = options.Body2Cce ?? doubleQuat.Identity;
            double3 bodyRates = options.BodyRates;
            if (surface is SurfaceRequest request)
            {
                Vehicle.InitialKinematicState state = ComputeSurfaceState(tree, request.Body, request.LatitudeDeg,
                    request.LongitudeDeg, request.HeadingDeg, request.HeightAboveTerrain, Universe.GetElapsedTime());
                orbit = state.Orbit;
                body2Cce = state.Body2Cce;
                bodyRates = state.BodyRates;
            }

            vehicle = Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, tree.Root,
                orbit ?? throw new InvalidOperationException($"no orbit for '{id}'."));

            // The sequence list is built lazily. SetActiveSequence on a list that is not built yet
            // advances the active sequence once more for every sequence the build then adds at or
            // below it (SequenceList.Add), so the list is built first, while the active sequence is
            // still 0.
            vehicle.Parts.EnsureDerived(DerivedData.Sequences);
            if (data != null)
            {
                vehicle.Parts.SequenceList.SetActiveSequence(data.ActiveSequence);
                vehicle.Parts.SequenceList.ApplyEnvironments(data.SequenceEnvironments);
                vehicle.Parts.FuelLinks.ApplySaveData(data.FuelLinks, data.RootPartInstance);
                // VehicleTemplate.CreateInto has no mission; this restores it as
                // PartTree.Deserialize(PartTreeData) does.
                vehicle.Parts.Mission = data.Mission?.Clone();
            }
            parent.Children.Add(vehicle);

            if (options.SeatCrew)
            {
                SeatRandomCrew(vehicle);
            }
            else
            {
                int cleared = ClearForeignSeats(vehicle);
                if (cleared > 0)
                    HarnessLog.Line($"[harness] '{vehicle.Id}' cleared {cleared} seat(s) that named a kitten not crewing this vehicle; no crew seated.");
            }
            return vehicle;
        }
        catch
        {
            RollBackSpawn(system, id, vehicle, tree);
            throw;
        }
    }

    // A vehicle the constructor returned goes through the normal destroy path. A constructor that
    // threw after Astronomical's constructor registered the half-built vehicle leaves only the
    // registration and the part tree, so both are removed by hand.
    private static void RollBackSpawn(CelestialSystem system, string id, Vehicle? vehicle, PartTree tree)
    {
        try
        {
            if (vehicle != null)
            {
                Despawn(vehicle);
                return;
            }
            Astronomical? registered = system.All.Get(id);
            if (registered != null)
                system.All.Deregister(registered);
            if (!tree.IsDisposed)
                tree.Dispose();
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] rollback of the failed spawn '{id}' threw as well: {e.Message}");
        }
    }
}
