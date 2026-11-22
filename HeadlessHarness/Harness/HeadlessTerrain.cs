using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// The procedural terrain modifiers of the bodies (craters, noise, erosion, tiling detail and the
// flat decals of the launch sites), evaluated on the CPU without a renderer.
//
// Celestial.GetTerrainHeightFromDirCcf applies the modifiers only for
// i < Astronomical.TerrainModifiersRenderData.NumModifiers, and only Astronomical.SetupModifierRenderData
// makes that object (from Universe.SetupRenderData, and from Vehicle.GetSituationState when it is
// null). Its constructor, ProceduralModifiersRenderData(Renderer, TerrainReference), makes the Vulkan
// texture of the curve table (CreateCurveLut), so without a renderer the property stays null and every
// terrain height is the bare heightmap. The pad pose of ConstraintSim.ResolveLaunchPads, the terrain
// colliders and Vehicle.GetInitialKinematicStateForLocation then use the wrong ground.
//
// The CPU path reads only NumModifiers, ModifierCurveData and LutTextureSize of that object
// (Celestial.GetTerrainHeightFromDirCcf, and the Evaluate methods of the modifiers through
// ProceduralModifiersRenderData.EvaluateLutGrad); the readers of the textures and image views are
// all renderers. So each body with a TerrainReference gets an object made without its constructor
// and filled as ProceduralModifiersRenderData.SetupCelestialTerrainMods fills it, without the GPU
// textures and the upload. The runner keeps this state between tests, and
// ProceduralModifiersRenderData.Dispose is skipped for these objects, because they hold no GPU
// resource for StaticCelestial.Dispose to free.
public static class HeadlessTerrain
{
    // "1" (or "true") keeps the bare heightmap: no body gets the CPU modifier data. Unset, blank, "0"
    // or "false" gives every body its modifiers. Anything else is an infrastructure failure.
    public const string BareTerrainEnvVar = "KSA_HEADLESS_BARE_TERRAIN";

    // ProceduralModifiersRenderData.CreateCurveLutInternal: 128 rows of R16G16 half floats (4 bytes
    // a texel), four columns a modifier (NUM_FLOAT_CURVES).
    private const int CurveRows = ProceduralModifiersRenderData.LUT_RESOLUTION;
    private const int BytesPerTexel = ProceduralModifiersRenderData.LUT_BPP;
    private const int ColumnsPerModifier = ProceduralModifiersRenderData.NUM_FLOAT_CURVES;

    // The test thread installs and releases between solver steps, but the Dispose prefix runs on
    // whichever thread disposes a body, so the shared state is locked.
    private static readonly object Gate = new();

    // The objects made here, which ProceduralModifiersRenderData.Dispose must not touch.
    private static readonly ConditionalWeakTable<ProceduralModifiersRenderData, object> Made = new();
    private static readonly object MadeMark = new();

    // The bodies that hold an object made here, with that object.
    private static readonly Dictionary<Celestial, ProceduralModifiersRenderData> Installed = new(ReferenceEqualityComparer.Instance);

    // The bodies that got the data when the system loaded, which the runner gives it back to after
    // a test that released it.
    private static readonly List<Celestial> RunBodies = new();

    private static MethodInfo? _setter;
    private static FieldInfo? _curveDataField;
    private static FieldInfo? _renderParamsField;
    private static FieldInfo? _terrainReferenceField;

    private static bool? _enabled;

    // True when the run gives the bodies their terrain modifiers (the default), false when
    // KSA_HEADLESS_BARE_TERRAIN keeps the bare heightmap.
    public static bool Enabled => _enabled ??= !ParseSwitch(Environment.GetEnvironmentVariable(BareTerrainEnvVar));

    // True when the body holds the CPU modifier data of this class.
    public static bool HasCpuModifiers(Celestial body)
    {
        lock (Gate)
            return Installed.TryGetValue(body, out ProceduralModifiersRenderData? data) && ReferenceEquals(body.TerrainModifiersRenderData, data);
    }

    // Gives the body the CPU part of its terrain modifier data when it has none, and says what it
    // did. A body that already holds modifier data keeps it. Throws MissingMemberException when a
    // game member it needs is gone.
    public static string Install(Celestial body)
    {
        if (body.TerrainModifiersRenderData != null)
            return $"{body.Id} already has its modifier data ({body.TerrainModifiersRenderData.NumModifiers} modifiers), kept as it is";
        if (body.BodyTemplate?.TerrainReference is not TerrainReference terrain)
            return $"{body.Id} has no terrain reference, so it has no terrain modifier";
        BindMembers();

        // The count and the order of the game: NumModifiers comes from
        // ProceduralModifiersRenderData.UpdateModifierCount, the curves and the render parameters
        // from the sorted TerrainReference.TerrainModifiers.
        int count = terrain.ProceduralModifiers.ModifierCount;
        List<ModifierReference> modifiers = terrain.TerrainModifiers;
        int width = count == 0 ? ColumnsPerModifier : count * ColumnsPerModifier;
        byte[] curves = new byte[width * CurveRows * BytesPerTexel];
        // ProceduralModifiersRenderData.ClearLut, the two other bytes of each texel stay 0.
        for (int i = 0; i < curves.Length; i += BytesPerTexel)
        {
            curves[i] = byte.MaxValue;
            curves[i + 1] = byte.MaxValue;
        }

        List<ProceduralModifiersRenderData.ModifierRenderParams> renderParams = new(modifiers.Count);
        int textureIndex = 0;
        for (int i = 0; i < modifiers.Count; i++)
        {
            ModifierReference modifier = modifiers[i];
            renderParams.Add(modifier.ToRenderData(i, textureIndex));
            List<CubicHermiteSpline> modifierCurves = modifier.Curves;
            for (int j = 0; j < modifierCurves.Count; j++)
                modifierCurves[j].WriteValuesToByteArray(curves, ProceduralModifiersRenderData.LutFormat,
                    i * ColumnsPerModifier + j, count * ColumnsPerModifier);
            if ((modifier.Requirements & ProceduralModifiersRenderData.ModifierRequirements.Texture) != 0)
                textureIndex += modifier.TextureReferences?.Count ?? 0;
        }

        var data = (ProceduralModifiersRenderData)RuntimeHelpers.GetUninitializedObject(typeof(ProceduralModifiersRenderData));
        data.NumModifiers = count;
        data.LutTextureSize = new int2(width, CurveRows);
        _curveDataField!.SetValue(data, curves);
        _renderParamsField!.SetValue(data, renderParams);
        _terrainReferenceField?.SetValue(data, terrain);
        lock (Gate)
        {
            Made.AddOrUpdate(data, MadeMark);
            _setter!.Invoke(body, new object?[] { data });
            Installed[body] = data;
        }
        return $"{body.Id}: {count} modifiers, curve table {width} x {CurveRows}";
    }

    // Puts the null back on a body that holds the data of Install. Returns false when it held none.
    public static bool Release(Celestial body)
    {
        lock (Gate)
        {
            if (!Installed.Remove(body, out ProceduralModifiersRenderData? data))
                return false;
            if (!ReferenceEquals(body.TerrainModifiersRenderData, data))
                return false;
            _setter!.Invoke(body, new object?[] { null });
            return true;
        }
    }

    // Takes the data of Install off every body. The runner calls it at the end of the run.
    public static int ReleaseAll()
    {
        List<Celestial> bodies;
        lock (Gate)
            bodies = new List<Celestial>(Installed.Keys);
        int released = 0;
        foreach (Celestial body in bodies)
        {
            if (Release(body))
                released++;
        }
        return released;
    }

    // Called when Universe.LoadSystem made the bodies. Gives every body with a TerrainReference its
    // modifier data unless the run keeps the bare heightmap. That is every body the game can give the
    // data to: Universe.SetupRenderData covers the billboarded ones, and Vehicle.GetSituationState any
    // body a vehicle starts landed on. A body whose terrain height throws with the data is left bare,
    // with a warning, so that one broken modifier does not stop every test that never touches it.
    internal static void OnSystemLoaded(CelestialSystem? system)
    {
        lock (Gate)
        {
            Installed.Clear();
            RunBodies.Clear();
        }
        if (system == null)
            return;
        if (!Enabled)
        {
            HarnessLog.Line($"[bringup] terrain modifiers: off ({BareTerrainEnvVar}), every terrain height is the bare heightmap.");
            return;
        }

        List<string> made = new();
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Celestial body || body.TerrainModifiersRenderData != null
                || body.BodyTemplate?.TerrainReference == null)
                continue;
            string how = Install(body);
            try
            {
                // A sample runs only the modifiers whose biomes cover the point, and a decal only near
                // its centre, so this finds a modifier that throws at one of these points, not every
                // one that could throw somewhere.
                body.GetTerrainHeightFromDirCcf(double3.UnitX);
                foreach (ModifierReference modifier in body.BodyTemplate.TerrainReference.TerrainModifiers)
                {
                    if (modifier is DecalModifierReference { Location: not null } decal)
                        body.GetTerrainHeightFromDirCcf(decal.Location.ForwardCcf);
                }
            }
            catch (Exception e)
            {
                Release(body);
                HarnessLog.Line($"[bringup] WARNING: the terrain height of '{body.Id}' throws with its modifiers, so it keeps the bare heightmap:\n{e}");
                continue;
            }
            lock (Gate)
                RunBodies.Add(body);
            made.Add(how);
        }
        HarnessLog.Line($"[bringup] terrain modifiers: evaluated on the CPU for {made.Count} bod{(made.Count == 1 ? "y" : "ies")}" +
                        (made.Count > 0 ? $" ({string.Join("; ", made)})" : "") + $". {BareTerrainEnvVar}=1 keeps the bare heightmap.");
    }

    // Puts the run's terrain state back after a test: a body that had the data at the system load
    // gets it again when a test released it, and with the bare heightmap a body a test gave the data
    // loses it again. Returns true when something was put back.
    internal static bool RestoreRunState(string after)
    {
        List<Celestial> restore = new();
        List<Celestial> remove = new();
        lock (Gate)
        {
            foreach (Celestial body in RunBodies)
            {
                if (body.TerrainModifiersRenderData == null)
                    restore.Add(body);
            }
            if (!Enabled)
                remove.AddRange(Installed.Keys);
        }
        foreach (Celestial body in restore)
        {
            Install(body);
            HarnessLog.Line($"[harness] {after} left '{body.Id}' without its terrain modifier data; made it again.");
        }
        foreach (Celestial body in remove)
        {
            if (Release(body))
                HarnessLog.Line($"[harness] {after} left the CPU terrain modifier data on '{body.Id}' in a bare terrain run; released it.");
        }
        return restore.Count > 0 || remove.Count > 0;
    }

    // The prefix on ProceduralModifiersRenderData.Dispose: the objects made here have no curve
    // texture, image view or renderer, so the stock method would throw on them.
    internal static bool DisposeUnlessCpuOnly(ProceduralModifiersRenderData __instance) => !Made.TryGetValue(__instance, out _);

    internal static void InstallPatches(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(ProceduralModifiersRenderData), nameof(ProceduralModifiersRenderData.Dispose))
                ?? throw new MissingMethodException("ProceduralModifiersRenderData.Dispose not found - game version may have changed."),
            prefix: new HarmonyMethod(typeof(HeadlessTerrain), nameof(DisposeUnlessCpuOnly)));
    }

    // Reads the switch before the long content load, so a typo fails at once.
    internal static void ReadSwitch() => _ = Enabled;

    internal static bool ParseSwitch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
                return true;
            case "0":
            case "false":
                return false;
            default:
                throw new InvalidOperationException(
                    $"[HeadlessHarness] {BareTerrainEnvVar}='{value}' is not 1, true, 0 or false.");
        }
    }

    // The private setter of Astronomical.TerrainModifiersRenderData and the readonly fields of
    // ProceduralModifiersRenderData. _terrainReference is optional: only UpdateModifierCount and
    // SetupCelestialTerrainMods read it, and no CPU path calls them.
    private static void BindMembers()
    {
        if (_setter != null)
            return;
        MethodInfo? setter = typeof(Astronomical).GetProperty(nameof(Astronomical.TerrainModifiersRenderData))?.GetSetMethod(nonPublic: true);
        FieldInfo? curveData = typeof(ProceduralModifiersRenderData).GetField(nameof(ProceduralModifiersRenderData.ModifierCurveData));
        FieldInfo? renderParams = typeof(ProceduralModifiersRenderData).GetField(nameof(ProceduralModifiersRenderData.ModifierRenderData));
        if (setter == null || curveData == null || renderParams == null)
            throw new MissingMemberException(
                "Astronomical.TerrainModifiersRenderData (private setter), ProceduralModifiersRenderData.ModifierCurveData or " +
                $"ProceduralModifiersRenderData.ModifierRenderData not found - game version may have changed. {BareTerrainEnvVar}=1 runs without the terrain modifiers.");
        _curveDataField = curveData;
        _renderParamsField = renderParams;
        _terrainReferenceField = typeof(ProceduralModifiersRenderData).GetField("_terrainReference", BindingFlags.Instance | BindingFlags.NonPublic);
        _setter = setter;
    }
}
