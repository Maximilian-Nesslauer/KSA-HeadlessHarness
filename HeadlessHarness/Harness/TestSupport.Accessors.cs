using System.Reflection;
using HarmonyLib;
using KSA;

namespace HeadlessHarness.Harness;

// Typed access to the game privates that tests read by hand. Each member is bound by name on first
// use, so a rename fails only the tests that use it, with the drift message, and every consumer
// shares one binding that is re-verified once per game update.
//
// The solver writes the update state of a vehicle on worker threads during SimDriver.Step. Read it
// and call the part tree members between steps only, as the game UI does after Program.PrepareFrame.
public static partial class TestSupport
{
    private static AccessTools.FieldRef<Vehicle, ManualControlInputs>? _accessorManualInputs;
    private static AccessTools.FieldRef<Vehicle, VehicleUpdateState>? _accessorUpdateState;
    private static Action<PartTree>? _accessorRecreateResourceManagers;
    private static Action? _accessorFlushDirtyResourceManagers;
    private static FieldInfo? _accessorStaticObjectCollider;

    // Inputs queued by one test must not apply on the first step of another test. TypedBuffer is a
    // struct, so Reset runs on a boxed value which is then written back to its static field.
    public static void DiscardPendingInputs()
    {
        foreach (FieldInfo field in typeof(InputEvents).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Type type = field.FieldType;
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(InputEvents.TypedBuffer<>))
                continue;
            object buffer = field.GetValue(null)!;
            MethodInfo reset = type.GetMethod("Reset", Type.EmptyTypes)
                ?? throw AccessorDrift("InputEvents.TypedBuffer.Reset");
            reset.Invoke(buffer, null);
            field.SetValue(null, buffer);
        }
    }

    // The manual inputs (throttle, engine switch, thruster flags) that Vehicle.PrepareWorker hands to
    // the flight computer at the next step. A write through the reference is the headless equivalent
    // of the player's input, without the full replacement SetManualControlInputs makes.
    public static ref ManualControlInputs GetManualControlInputs(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        _accessorManualInputs ??= BindVehicleField<ManualControlInputs>("_manualControlInputs");
        return ref _accessorManualInputs(vehicle);
    }

    // The per-vehicle solver state behind Vehicle._threadWorkerUpdateState. Null until the game first
    // configures the vehicle (Vehicle.UpdateVehicleConfiguration), which every spawn path does.
    public static VehicleUpdateState? GetUpdateState(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        _accessorUpdateState ??= BindVehicleField<VehicleUpdateState>("_threadWorkerUpdateState");
        return _accessorUpdateState(vehicle);
    }

    // True when the last step touched another vehicle. The solver clears
    // VehicleUpdateState.HadVehicleContactThisFrame at the start of each step and sets it for every
    // contact with another vehicle in the bubble.
    public static bool HadVehicleContact(Vehicle vehicle)
        => GetUpdateState(vehicle)?.HadVehicleContactThisFrame ?? false;

    // True while the physics of the vehicle holds a static object collider, the launch pad that
    // ConstraintSim.UpdateStaticObjectCollider adds for a nearby landmark (BepuHandles.StaticObjectCollider).
    // The field is a nullable BepuPhysics.StaticHandle, read by reflection so that the harness needs
    // no reference to BepuPhysics; a boxed empty nullable is null.
    public static bool HasStaticCollider(Vehicle vehicle)
    {
        if (GetUpdateState(vehicle) is not { } state)
            return false;
        _accessorStaticObjectCollider ??= AccessTools.Field(typeof(BepuHandles), "StaticObjectCollider")
            ?? throw AccessorDrift("BepuHandles.StaticObjectCollider");
        return _accessorStaticObjectCollider.GetValue(state.BepuHandles) != null;
    }

    // The bubble the vehicle is simulated in, or null before its first step and while it is on rails
    // outside any bubble. The game exposes only the flag (Vehicle.HasPhysicsBubble).
    public static PhysicsBubble? GetPhysicsBubble(Vehicle vehicle)
        => GetUpdateState(vehicle)?.Bubble;

    // Rebuilds the resource managers of a tree after a test changed its parts or tanks by hand, as
    // EVADoor.GetBackPackPart does for a backpack tree it builds (internal PartTree.RecreateResourceManagers).
    public static void RecreateResourceManagers(PartTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _accessorRecreateResourceManagers ??=
            AccessTools.Method(typeof(PartTree), "RecreateResourceManagers", Type.EmptyTypes)?.CreateDelegate<Action<PartTree>>()
            ?? throw AccessorDrift("PartTree.RecreateResourceManagers");
        _accessorRecreateResourceManagers(tree);
    }

    // Rebuilds the resource managers of every tree marked dirty, the flush Program.PrepareFrame runs
    // before the vehicle solvers and SimDriver.Step runs before and after them (internal static
    // PartTree.FlushDirtyResourceManagers).
    public static void FlushDirtyResourceManagers()
    {
        _accessorFlushDirtyResourceManagers ??=
            AccessTools.Method(typeof(PartTree), "FlushDirtyResourceManagers", Type.EmptyTypes)?.CreateDelegate<Action>()
            ?? throw AccessorDrift("PartTree.FlushDirtyResourceManagers");
        _accessorFlushDirtyResourceManagers();
    }

    // AccessTools.FieldRefAccess throws its own exception types for a missing or retyped field; both
    // are reported as drift, so a test reads them the same way as every other drift key.
    private static AccessTools.FieldRef<Vehicle, TField> BindVehicleField<TField>(string name)
    {
        if (AccessTools.Field(typeof(Vehicle), name) is not { } field || field.FieldType != typeof(TField))
            throw AccessorDrift($"Vehicle.{name} ({typeof(TField).Name})");
        try
        {
            return AccessTools.FieldRefAccess<Vehicle, TField>(field);
        }
        catch (Exception e) when (e is ArgumentException or MissingFieldException)
        {
            throw AccessorDrift($"Vehicle.{name} ({typeof(TField).Name})");
        }
    }

    private static InvalidOperationException AccessorDrift(string member)
        => new($"[HeadlessHarness] {member} not found - game version may have changed.");
}
