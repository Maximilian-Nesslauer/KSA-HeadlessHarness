using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;
using KSA.Rendering.Water.Rendering;

namespace HeadlessHarness.Harness;

// Owns the GPU-free bring-up of KSA. Never constructs Program/App/Renderer. Runs the same CPU-only
// load calls the real Program..ctor makes, in dependency order, skipping every GPU/window/ImGui
// step. Verified against the build pinned in Mod.TestedGameVersion, where all the load calls are
// public static. Several body/vehicle types build a GPU render component in their constructor, and
// the load path touches the ImGui console / loading screen; each is neutralized by a Harmony patch
// (or a headless stand-in) installed before the load runs. Every patch target and reflection key is
// re-checked on game update.
//
// Where the game holds a render object behind an interface, the bring-up supplies a CPU-only
// implementation rather than intercepting callers: RegisterHeadlessViewport is what makes
// Program.MainViewport and the camera accessors resolve. Only the two renderer accessors, which
// have no such seam, are still patched.
//
// One-shot per process: the game globals initialized here are never torn down, a second BringUp is
// not supported, and the patches stay installed until the process exits (which the harness does
// right after the suite).
public sealed class HeadlessSession
{
    private const string HarmonyId = "com.maxi.headlessharness.session";

    private static Exception? _lastLoggedBodyException;

    // The one-shot rule is per process, not per instance: a second bring-up would re-run the
    // application-start calls, take another of the registry's eight shader slots and overwrite the
    // main-viewport field. Enforced, because this class is public.
    private static HeadlessSession? _broughtUpSession;

    public bool IsBroughtUp { get; private set; }

    // Null until BringUp has run. Public so a test can reach the cameras the game itself uses.
    public HeadlessViewport? MainViewport { get; private set; }

    public CelestialSystem System =>
        Universe.CurrentSystem ?? throw new InvalidOperationException(
            IsBroughtUp ? "No system is loaded." : "BringUp has not run.");

    // systemId null => the first loaded system template. SystemLibrary.Default is only ever assigned
    // from the game's system-select popup, which no headless run reaches, so it stays null here and
    // the fallback is what actually decides. Name a system explicitly to pin one.
    public void BringUp(string? systemId = null)
    {
        if (IsBroughtUp)
            return;
        if (_broughtUpSession != null && !ReferenceEquals(_broughtUpSession, this))
            throw new InvalidOperationException(
                "[HeadlessHarness] another HeadlessSession has already been brought up in this " +
                "process, and the game globals it initialized are never torn down. Reuse that one.");
        _broughtUpSession = this;

        InstallHeadlessPatches();

        HarnessLog.Line("[bringup] application-start");
        // Registers the Tomlet mapper for KeyBindingValue, which GameSettings holds a dictionary of
        // and cannot round-trip without it (no parameterless constructor, get-only properties).
        // LoadFromFile writes the settings file back out.
        Input.OnApplicationStart();
        GameSettings.OnApplicationStart();
        GameSettings.LoadFromFile();
        GameSaves.OnApplicationStart();
        ModLibrary.OnApplicationStart();
        Languages.OnApplicationStart();
        VehicleSaves.OnApplicationStart();
        DefaultVehicleSaves.OnApplicationStart();
        LayoutSaves.OnApplicationStart();
        GameAudio.OnApplicationStart();

        HarnessLog.Line("[bringup] cpu asset load");
        ModLibrary.PrepareAll();
        ModLibrary.PreloadAssetBundles();
        ModLibrary.PreloadLanguages();
        ModLibrary.LoadEditorTags();
        ModLibrary.LoadAll();
        ModLibrary.AssignDefaults();

        // Program..ctor builds its viewports here, and the registry must hold one before anything
        // loads a system or ticks a solver.
        HarnessLog.Line("[bringup] headless viewport");
        MainViewport = RegisterHeadlessViewport();

        // Program..ctor initializes the job systems here, after the viewports and before the populate
        // calls. Match that position: if a future game version made any populate/substance call
        // dispatch parallel work, running Initialize later would leave a null scheduler.
        HarnessLog.Line("[bringup] job systems");
        JobSystems.Initialize();

        ModLibrary.PopulateMeshCollections();
        ModLibrary.PopulateHeightmapCollections();
        SystemLibrary.PopulateMeshCollections();
        SystemLibrary.PopulateHeightmapCollections();
        ModLibrary.AttachGameData();
        ModLibrary.PopulateSounds();
        SubstanceLibrary.LoadAll();
        // Paired with SubstanceLibrary.LoadAll in the game's own bring-up. Without it any part
        // carrying a SolidGrainSegment throws "No grain geometries loaded" out of its constructor,
        // so a save with a solid motor cannot be deserialized at all.
        GrainGeometryLibrary.LoadAll();

        string id = systemId ?? SystemLibrary.Default?.Id ?? SystemLibrary.First().Id;
        HarnessLog.Line($"[bringup] load system '{id}'");
        Universe.LoadSystem(id);

        IsBroughtUp = true;
        HarnessLog.Line($"[bringup] complete, system='{Universe.CurrentSystem?.Id}'");
    }

    public SimDriver CreateDriver()
    {
        if (!IsBroughtUp)
            throw new InvalidOperationException("Call BringUp before CreateDriver.");
        return new SimDriver(Universe.GetElapsedTime());
    }

    // An empty registry makes MainViewport throw and every GameViews loop silently do nothing. The
    // registry's creation entry points all take a Renderer, so its three internal steps are reached
    // directly instead: Allocate, Register, and the main-viewport field. Each is a drift key.
    private static HeadlessViewport RegisterHeadlessViewport()
    {
        MethodInfo allocate = AccessTools.Method(typeof(ViewportRegistry), "Allocate")
            ?? throw Drift("ViewportRegistry.Allocate");
        MethodInfo register = AccessTools.Method(typeof(ViewportRegistry), "Register", new[] { typeof(IViewport) })
            ?? throw Drift("ViewportRegistry.Register(IViewport)");
        FieldInfo mainViewportField = AccessTools.Field(typeof(ViewportRegistry), "_mainViewport")
            ?? throw Drift("ViewportRegistry._mainViewport");

        // ViewportAllocation is internal, so its values are read off the boxed struct by name. A
        // struct return never boxes to null.
        object allocation = allocate.Invoke(null, null)
            ?? throw Drift("ViewportRegistry.Allocate no longer returns a ViewportAllocation");
        ViewportId id = ReadAllocationMember<ViewportId>(allocation, "Id");
        int shaderSlot = ReadAllocationMember<int>(allocation, "ShaderSlot");

        HeadlessViewport viewport = new HeadlessViewport(
            id, shaderSlot, new int2(HeadlessViewport.DefaultWidth, HeadlessViewport.DefaultHeight));
        register.Invoke(null, new object[] { viewport });
        mainViewportField.SetValue(null, viewport);

        // The game marks its own main viewport visible right after building it.
        viewport.SetVisible(true);

        if (!ReferenceEquals(Program.MainViewport, viewport))
            throw new InvalidOperationException(
                "[HeadlessHarness] the headless viewport did not become Program.MainViewport - " +
                "game version may have changed.");

        HarnessLog.Line($"[bringup] registered headless main viewport id={id.Value} slot={shaderSlot} " +
                        $"{viewport.Width}x{viewport.Height}.");
        return viewport;
    }

    // A hard cast would surface a retyped member as an InvalidCastException, reading as a harness
    // bug rather than as game drift.
    private static T ReadAllocationMember<T>(object allocation, string member)
    {
        object? value = (AccessTools.Property(allocation.GetType(), member)
            ?? throw Drift($"ViewportAllocation.{member}")).GetValue(allocation);
        if (value is not T typed)
            throw Drift($"ViewportAllocation.{member} is {value?.GetType().Name ?? "null"}, expected {typeof(T).Name}");
        return typed;
    }

    private void InstallHeadlessPatches()
    {
        Harmony harmony = new Harmony(HarmonyId);

        // Vehicle.PrepareWorker reads ImGui.GetIO().WantCaptureKeyboard for the
        // controlled vehicle while this is true, and headless there is no ImGui
        // context to read. False is also the honest headless state: no player is
        // flying. It only costs the held thruster/throttle flags being cleared
        // each prepare, which no test drives (TestSupport writes EngineOn and
        // EngineThrottle, which ClearHeldPlayerInput does not touch).
        Program.IsControlledVehicleActive = false;

        // Universe.LoadSystem calls Universe.OnLoaded, which runs follow/control terminal commands
        // through Program.TerminalInterface (the ImGui console). Headless there is no console, so skip
        // OnLoaded; LoadSystem still sets CurrentSystem/WorldSun before calling it.
        harmony.Patch(
            AccessTools.Method(typeof(Universe), nameof(Universe.OnLoaded)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(Skip)));

        // The loading screen is a GPU/ImGui object built only by the Loading ctor (needs a Renderer).
        // FileReference.Load and friends call Loading.Task, whose LoadTask ctor throws
        // "LoadWindow not initialized" when Loading.Current is null. Install a headless Loading.Current
        // (an uninitialized instance with only its task list) and no-op the one GPU method, OnFrame, so
        // the whole progress-task path (Task/PushTask/State/Pop) works without a renderer.
        InstallHeadlessLoading(harmony);

        // StaticCelestial..ctor (every planet) builds a DistantSphereRenderer, and KittenEva..ctor
        // builds a KittenRenderable - both deref the null headless RendererContext / Program.Instance.
        // Their fields are only used at render time, so skip the ctors. A body failing deep in a
        // subtree would otherwise abort every sibling body after it (the ctor catches per root tree).
        harmony.Patch(SoleConstructor(typeof(DistantSphereRenderer)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(Skip)));
        harmony.Patch(SoleConstructor(typeof(KittenRenderable)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(Skip)));

        // The renderers hang off a null Program.Instance, so the two accessors the sim path reaches
        // answer null, which both call sites already handle:
        //   Vehicle.UpdateNavballData -> GetRadarAltitude -> Program.GetOceanRenderer(), no ocean height.
        //   PhysicsBubble.SyncGroundClutterStatics -> Program.GetPlanetRenderer(), on the solver path
        //   once a bubble rents a constraint sim; a null one clears the bubble's clutter colliders,
        //   so a surface test never sees a clutter collision.
        // The cameras need no patch, RegisterHeadlessViewport gives them a real viewport.
        harmony.Patch(
            AccessTools.Method(typeof(Program), nameof(Program.GetOceanRenderer)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(ReturnNullOceanRenderer)));
        harmony.Patch(
            AccessTools.Method(typeof(Program), nameof(Program.GetPlanetRenderer)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(ReturnNullPlanetRenderer)));

        // Decoupler.Decouple performs the split (Vehicle.Split, pure sim), then plays a sound and
        // spawns separation particles through Program.Instance.ParticleSystem, which is null headless.
        // Replace it with just the split so the game's real staging path (SequenceList.
        // ActivateNextSequence -> Part.ActivateSubtreeInStage -> Decoupler.SetIsActive -> input buffer ->
        // IActivateInputData.Apply -> Decoupler.Decouple) works without a renderer.
        harmony.Patch(
            AccessTools.Method(typeof(Decoupler), nameof(Decoupler.Decouple)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(HeadlessDecouple)));

        // Diagnostic: CelestialSystem..ctor catches per-root-tree exceptions and skips the rest, so a
        // body that throws during construction silently drops itself and its siblings. Surface it for
        // both child bodies (CreateTreeFrom) and root bodies (CreateTreeFromRoot builds the root itself
        // via CreateInto, outside CreateTreeFrom).
        harmony.Patch(
            AccessTools.Method(typeof(CelestialSystem), nameof(CelestialSystem.CreateTreeFrom)),
            finalizer: new HarmonyMethod(typeof(HeadlessSession), nameof(LogChildBodyException)));
        harmony.Patch(
            AccessTools.Method(typeof(CelestialSystem), nameof(CelestialSystem.CreateTreeFromRoot)),
            finalizer: new HarmonyMethod(typeof(HeadlessSession), nameof(LogRootBodyException)));
    }

    private void InstallHeadlessLoading(Harmony harmony)
    {
        Loading headlessLoading = (Loading)RuntimeHelpers.GetUninitializedObject(typeof(Loading));
        FieldInfo tasksField = AccessTools.Field(typeof(Loading), "_tasks")
            ?? throw Drift("Loading._tasks");
        tasksField.SetValue(headlessLoading, new List<LoadTask>());
        MethodInfo currentSetter = AccessTools.PropertySetter(typeof(Loading), nameof(Loading.Current))
            ?? throw Drift("Loading.Current setter");
        currentSetter.Invoke(null, new object[] { headlessLoading });

        harmony.Patch(
            AccessTools.Method(typeof(Loading), nameof(Loading.OnFrame)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(Skip)));
    }

    private static ConstructorInfo SoleConstructor(Type type)
    {
        ConstructorInfo[] ctors = type.GetConstructors();
        if (ctors.Length != 1)
            throw new InvalidOperationException(
                $"[HeadlessHarness] expected exactly one public constructor on {type.Name}, found " +
                $"{ctors.Length} - game version may have changed.");
        return ctors[0];
    }

    private static Exception Drift(string what) =>
        new InvalidOperationException($"[HeadlessHarness] {what} not found - game version may have changed.");

    private static bool Skip() => false;

    private static bool ReturnNullOceanRenderer(ref OceanRenderer? __result)
    {
        __result = null;
        return false;
    }

    private static bool ReturnNullPlanetRenderer(ref PlanetRenderer? __result)
    {
        __result = null;
        return false;
    }

    // The split half of Decoupler.Decouple, minus its audio/particle presentation (see the patch
    // comment above). Connector and Force are public fields on Decoupler.
    //
    // The vehicle to split comes from the part tree, not from the oldVehicle argument, mirroring
    // stock. When two decouplers fire in the same sequence the upstream one splits first, so by the
    // time the downstream one runs its part already belongs to the shed vehicle and oldVehicle no
    // longer owns the connector, which makes its Split find nothing to detach.
    private static bool HeadlessDecouple(Decoupler __instance, Vehicle oldVehicle, ref Vehicle? __result)
    {
        Vehicle owner = __instance.Parent.FullPart.Tree.OwningVehicle ?? oldVehicle;
        __result = owner.Split(__instance.Connector, __instance.Force, out _);
        return false;
    }

    // The same exception object unwinds through every recursion frame; log it once (the innermost
    // frame fires first and names the failing body). Two finalizers because Harmony injects by
    // parameter name and the game builds child bodies via CreateTreeFrom(bodyTemplate) and root bodies
    // via CreateTreeFromRoot(rootTemplate).
    private static Exception? LogChildBodyException(Exception? __exception, AstronomicalTemplate bodyTemplate) =>
        LogBody(__exception, bodyTemplate);

    private static Exception? LogRootBodyException(Exception? __exception, AstronomicalTemplate rootTemplate) =>
        LogBody(__exception, rootTemplate);

    private static Exception? LogBody(Exception? exception, AstronomicalTemplate template)
    {
        if (exception != null && !ReferenceEquals(exception, _lastLoggedBodyException))
        {
            _lastLoggedBodyException = exception;
            HarnessLog.Line($"[bringup] body '{template.Id}' construction threw:\n{exception}");
        }
        return exception;
    }
}
