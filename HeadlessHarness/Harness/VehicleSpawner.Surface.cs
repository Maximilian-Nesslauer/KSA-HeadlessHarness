using System.Collections.Generic;
using Brutal.Numerics;
using KSA;

namespace HeadlessHarness.Harness;

// Landed spawns, placed the way VehicleLaunchMenu.LaunchVehicle places a launch: the mass
// properties and spawn bounds of the tree go into Vehicle.GetInitialKinematicStateForLocation,
// which stands the vehicle with its body +X along the local vertical and its bottom on the highest
// terrain under its footprint (on the deck when the point lies on a launch pad), moving with the
// turning body.
//
// The terrain is the one of a game with a renderer, site decals included, because the bring-up
// gives every body its terrain modifiers on the CPU (HeadlessTerrain). With
// KSA_HEADLESS_BARE_TERRAIN=1 it is the bare heightmap instead. Vehicle.GetLaunchPadHeightAtDirCcf
// adds the pad's own height either way.
public static partial class VehicleSpawner
{
    private readonly record struct SurfaceRequest(Celestial Body, double LatitudeDeg, double LongitudeDeg, double HeadingDeg, double? HeightAboveTerrain);

    // headingDeg is the compass direction of the body +Z axis, clockwise from north;
    // GetInitialKinematicStateForLocation points it north (heading 0) and the body +Y east.
    // heightAboveTerrain null rests the vehicle where the launch menu would; a value lifts it that
    // many meters higher, for a drop or for a start above a collider.
    public static Vehicle SpawnAtLocation(PartTree tree, Celestial body, double latitudeDeg, double longitudeDeg, double headingDeg,
        string id, double? heightAboveTerrain = null, SpawnOptions? options = null)
    {
        EnsureTreeFree(tree, id);
        options ??= SpawnOptions.Default;
        string vehicleId = PrepareId(body.System, id, options);
        return SpawnCore(tree, body.System, body, vehicleId, null,
            new SurfaceRequest(body, latitudeDeg, longitudeDeg, headingDeg, heightAboveTerrain), options, null);
    }

    public static Vehicle SpawnAtLocation(string saveId, Celestial body, double latitudeDeg, double longitudeDeg, double headingDeg,
        string id, double? heightAboveTerrain = null, SpawnOptions? options = null)
    {
        options ??= SpawnOptions.Default;
        VehicleSaveData data = ReadSave(saveId);
        PartTree tree = DeserializeSave(data, out string vehicleId, body.System, id, options);
        return SpawnCore(tree, body.System, body, vehicleId, null,
            new SurfaceRequest(body, latitudeDeg, longitudeDeg, headingDeg, heightAboveTerrain), options, data);
    }

    // Spawns at a named location of the body (CelestialTemplate.Locations, the list the launch
    // menu offers), heading north. A null id names the vehicle after the save, made unique the way
    // the launch menu does it.
    public static Vehicle SpawnAtLaunchSite(string saveId, Celestial body, string locationId, string? id = null, SpawnOptions? options = null)
    {
        LocationReference location = FindLocation(body, locationId);
        options ??= SpawnOptions.Default;
        if (id == null)
            options = options with { MakeIdUnique = true };
        return SpawnAtLocation(saveId, body, location.Latitude.ToDegrees(), location.Longitude.ToDegrees(), 0.0, id ?? saveId, null, options);
    }

    public static Vehicle SpawnAtLaunchSite(PartTree tree, Celestial body, string locationId, string id, SpawnOptions? options = null)
    {
        LocationReference location = FindLocation(body, locationId);
        return SpawnAtLocation(tree, body, location.Latitude.ToDegrees(), location.Longitude.ToDegrees(), 0.0, id, null, options);
    }

    public static LocationReference FindLocation(Celestial body, string locationId)
    {
        LocationReference? location = body.GetLocation(locationId);
        if (location != null)
            return location;
        List<string> known = new List<string>();
        if (body.BodyTemplate != null)
        {
            foreach (LocationReference candidate in body.BodyTemplate.Locations)
                known.Add(candidate.Key);
        }
        throw new InvalidOperationException(
            $"'{body.Id}' has no location '{locationId}'. Known: {(known.Count > 0 ? string.Join(", ", known) : "none")}.");
    }

    // The orbit, attitude and body rates a landed spawn of this tree gets. Tanks are read as they
    // are, so fill them first when the spawn fills them (SpawnCore does). The terrain is sampled
    // at the footprint corners of heading 0, as the game samples it; for a vehicle that is far
    // from round in plan, lift it with heightAboveTerrain.
    public static Vehicle.InitialKinematicState ComputeSurfaceState(PartTree tree, Celestial body, double latitudeDeg, double longitudeDeg,
        double headingDeg, double? heightAboveTerrain, UniverseTime time)
    {
        OffsetMassProperties mass = tree.ComputeInertMassPropertiesAsmb() + tree.ComputePropellantMassPropertiesAsmb();
        (double3 min, double3 max) = tree.ComputeSpawnBoundingBoxAsmb();
        Vehicle.InitialKinematicState state = Vehicle.GetInitialKinematicStateForLocation(body, time, latitudeDeg, longitudeDeg,
            min, max, double3.Unpack(in mass.Offset), OrbitLineColor);

        double3 angularVelocityCci = ((IParentBody)body).GetAngularVelocityCci();
        doubleQuat body2Cce = state.Body2Cce;
        double3 bodyRates = state.BodyRates;
        if (headingDeg != 0.0)
        {
            // A turn about body +X by -heading carries +Z (north) toward +Y (east).
            doubleQuat turn = doubleQuat.CreateFromAxisAngle(double3.UnitX, -headingDeg * Math.PI / 180.0);
            body2Cce = doubleQuat.Concatenate(turn, body2Cce);
            bodyRates = VehicleBuilder.Rotate(VehicleBuilder.Rotate(angularVelocityCci, body.GetCci2Cce()), body2Cce.Inverse());
        }

        Orbit orbit = state.Orbit;
        if (heightAboveTerrain is double lift && lift != 0.0)
        {
            double3 positionCci = orbit.StateVectors.PositionCci;
            positionCci += positionCci.Normalized() * lift;
            orbit = Orbit.CreateFromStateCci(body, time, positionCci, double3.Cross(angularVelocityCci, positionCci), OrbitLineColor);
        }

        return new Vehicle.InitialKinematicState
        {
            Orbit = orbit,
            Body2Cce = body2Cce,
            BodyRates = bodyRates,
        };
    }
}
