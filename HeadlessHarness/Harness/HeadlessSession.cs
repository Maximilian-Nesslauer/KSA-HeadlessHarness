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
// One-shot per process: the game globals initialized here are never torn down, a second BringUp is
// not supported, and the patches stay installed until the process exits (which the harness does
// right after the suite).
public sealed class HeadlessSession
{
    private const string HarmonyId = "com.maxi.headlessharness.session";

    // A dummy camera stands in for Program.GetMainCamera() headless (see InstallHeadlessPatches).
    // Static because the Harmony prefix that returns it must be static.
    private const int DummyViewportWidth = 1920;
    private const int DummyViewportHeight = 1080;
    private static Camera? _dummyCamera;
    private static Exception? _lastLoggedBodyException;

    public bool IsBroughtUp { get; private set; }

    public CelestialSystem System =>
        Universe.CurrentSystem ?? throw new InvalidOperationException(
            IsBroughtUp ? "No system is loaded." : "BringUp has not run.");

    // systemId null => the game's default system (Universe.LoadDefaultSystem uses
    // SystemLibrary.Default), falling back to the first loaded template.
    public void BringUp(string? systemId = null)
    {
        if (IsBroughtUp)
            return;

        InstallHeadlessPatches();

        HarnessLog.Line("[bringup] application-start");
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

        // Program..ctor initializes the job systems here, after AssignDefaults and before the populate
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
        return new SimDriver(Universe.GetElapsedSimTime());
    }

    private void InstallHeadlessPatches()
    {
        Harmony harmony = new Harmony(HarmonyId);
        _dummyCamera = new Camera(new int2(DummyViewportWidth, DummyViewportHeight));

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

        // The Vehicle path mixes sim with presentation and NREs headless because Program.Instance /
        // Program.Viewports are never set up:
        //   Vehicle.UpdateNavballData -> GetRadarAltitude -> Program.GetOceanRenderer() (null Instance)
        //   Vehicle.PrepareWorker -> Program.GetMainCamera() (Viewports empty). Only used to size the
        //   vehicle on screen for the useHighFidelityOceanPhysics flag; a fixed dummy camera keeps it
        //   deterministic. GetRadarAltitude already null-guards a null ocean renderer.
        harmony.Patch(
            AccessTools.Method(typeof(Program), nameof(Program.GetOceanRenderer)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(ReturnNullOceanRenderer)));
        harmony.Patch(
            AccessTools.Method(typeof(Program), nameof(Program.GetMainCamera)),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(ReturnDummyCamera)));

        // Decoupler.Decouple performs the split (Vehicle.Split, pure sim), then plays a sound and
        // spawns separation particles through Program.Instance.ParticleSystem, which is null headless.
        // Replace it with just the split so the game's real staging path (SequenceList.
        // ActivateNextSequence -> Part.ActivateInStage -> Decoupler.SetIsActive -> input buffer ->
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

    private static bool ReturnDummyCamera(ref Camera __result)
    {
        __result = _dummyCamera!;
        return false;
    }

    // The split half of Decoupler.Decouple, minus its audio/particle presentation (see the patch
    // comment above). Connector and Force are public fields on Decoupler.
    private static bool HeadlessDecouple(Decoupler __instance, Vehicle oldVehicle, ref Vehicle? __result)
    {
        __result = oldVehicle.Split(__instance.Connector, __instance.Force);
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
