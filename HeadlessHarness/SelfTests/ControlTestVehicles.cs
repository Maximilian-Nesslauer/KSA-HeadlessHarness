using Brutal.Numerics;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Builds the small stock vehicles the control self-tests fly, from part ids instead of a save, so
// the tests do not depend on what a profile's Vehicles folder holds. The part tree comes from a
// PartInstance through PartTree.Deserialize, the same path VehicleSpawner.SpawnFromSave takes.
internal static class ControlTestVehicles
{
    // Control, a 2500 J battery, a 3 W probe core power switch and avionics.
    public const string ProbeCorePart = "CoreCommandC_Prefab_ProbeCoreSmallA";

    // Mk1 Lander: one part with a habitat, one seat, a battery, a light and a control module.
    public const string PodPart = "CoreCommandB_Prefab_SmallLanderA";

    // Null with a logged reason when the loaded content lacks a part or the system has no home body.
    // Give each vehicle of one test its own altitude, so two of them never share a bubble.
    public static Vehicle? Spawn(string logTag, CelestialSystem system, string id, double altitudeM, params string[] partIds)
    {
        if (system.HomeBody is not IParentBody home)
        {
            Core.HarnessLog.Line($"[{logTag}] SKIP: the loaded system has no home body.");
            return null;
        }

        PartInstance root = new PartInstance { InstanceOf = partIds[0], LocalInstanceId = 1 };
        if (partIds.Length > 1)
            root.Children = new List<PartInstance>();
        for (int i = 1; i < partIds.Length; i++)
        {
            // No connectors: the parts are joined only in the tree, each 2 m further along -X,
            // which is all a control test needs.
            root.Children!.Add(new PartInstance
            {
                InstanceOf = partIds[i],
                LocalInstanceId = (uint)(i + 1),
                Parent = root,
                Transform = new TransformReference { PositionValue = new double3(-2.0 * i, 0.0, 0.0) },
            });
        }
        string? missing = root.FindMissingParts();
        if (missing != null)
        {
            Core.HarnessLog.Line($"[{logTag}] SKIP: the loaded content has no part {missing}.");
            return null;
        }

        PartTree tree = PartTree.Deserialize(root);
        Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + altitudeM, Universe.GetElapsedTime());
        Vehicle vehicle = Vehicle.CreateVehicle(system, doubleQuat.Identity, double3.Zero, home, id, tree.Root, orbit);
        home.Children.Add(vehicle);
        return vehicle;
    }
}
