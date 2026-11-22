using System.Collections.Generic;
using System.Linq;
using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// The terrain modifiers on the CPU (HeadlessTerrain), at the flat decals of the home body's launch
// sites. The run setting decides whether the home body holds the data (KSA_HEADLESS_BARE_TERRAIN);
// either way the test takes the data off, reads the bare heights at the decal centres, gives the
// data, checks that each decal the game applies there gives its flat, takes it off again, checks
// that the bare heights come back exactly, and leaves the body as the run setting has it.
public sealed class SpawnTerrainTest : IHarnessTest
{
    // DecalModifierReference.Evaluate sets the height to AltitudeOffset.Km where its feather weight is
    // 1 (the inner part of the decal), in float arithmetic on a height of a few hundred metres.
    private const double FlatTolerance = 0.02;

    // The weight below which Celestial.GetTerrainHeightFromDirCcf skips a modifier.
    private const float MinBiomeWeight = 1e-6f;

    public string Name => "spawn-terrain";

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        if (session.System.HomeBody is not Celestial home)
        {
            t.Skip("the loaded system has no home body");
            return t.Finish();
        }
        if (!home.HasTerrainHeightmap || home.BodyTemplate?.TerrainReference is not TerrainReference terrain
            || terrain.BiomeMaterials == null || terrain.BiomeMaterials.NumBiomeMaterials <= 0)
        {
            t.Skip($"{home.Id} has no heightmap, terrain reference or biome material, so the game applies no terrain modifier");
            return t.Finish();
        }
        // Celestial.GetTerrainHeightFromDirCcf returns the bare height before any modifier when the
        // normal map is not a loaded cubemap. A body with a terrain reference has one in stock, so
        // when it is not loaded the streamed body textures did not load and the run cannot test the
        // modifiers. Whether it is a cubemap is a content check of
        // TextureReference.ReadStreamedPayload, which reports a streamed texture that is not one.
        if (home.BodyTemplate.NormalReference?.Value?.IsLoaded() != true)
        {
            t.Infra($"the normal map of {home.Id} is not loaded, so the game applies no terrain modifier");
            return t.Finish();
        }

        // A decal that is not additive, follows the curvature and adds no height map flattens its
        // centre to AltitudeOffset.Km, which is the height in metres there, because
        // Celestial.GetTerrainHeightFromDirCcf passes MeanRadius in metres as the CelestialRadiusKm
        // of the modifiers. Km="75" is a flat at 75 m.
        List<DecalModifierReference> flats = terrain.TerrainModifiers.OfType<DecalModifierReference>()
            .Where(d => !d.Additive.Value && !d.NoCurvature.Value && d.Amplitude.Value == 0.0 && d.HeightMapTexture != null)
            .ToList();
        if (flats.Count == 0)
        {
            t.Skip($"{home.Id} has no flat decal");
            return t.Finish();
        }

        string? before = HeadlessTerrain.CheckRunState();
        if (before != null)
        {
            t.Infra($"the test starts from a terrain state that is not the run's: {before}");
            return t.Finish();
        }
        bool runHasData = HeadlessTerrain.HasCpuModifiers(home);
        if (HeadlessTerrain.Enabled && !runHasData)
        {
            t.Infra($"the run has the terrain modifiers on, but {home.Id} has no CPU modifier data (see the [bringup] terrain lines)");
            return t.Finish();
        }
        t.Check($"the run setting decides whether {home.Id} holds the CPU modifier data", runHasData == HeadlessTerrain.Enabled,
            $"{HeadlessTerrain.BareTerrainEnvVar} {(HeadlessTerrain.Enabled ? "unset" : "set")}, data {(runHasData ? "present" : "absent")}");

        try
        {
            if (runHasData)
                HeadlessTerrain.Release(home);
            if (!t.Check($"{home.Id} has no modifier data before the bare read", home.TerrainModifiersRenderData == null))
                return t.Finish();
            double3[] centres = flats.Select(d => d.Location.ForwardCcf).ToArray();
            double[] bare = centres.Select(c => home.GetTerrainHeightFromDirCcf(c)).ToArray();

            string how = HeadlessTerrain.Install(home);
            if (!t.Check("Install gives the CPU modifier data", HeadlessTerrain.HasCpuModifiers(home), how))
                return t.Finish();
            int applied = 0;
            int changed = 0;
            for (int i = 0; i < flats.Count; i++)
            {
                DecalModifierReference decal = flats[i];
                double height = home.GetTerrainHeightFromDirCcf(centres[i]);
                float weight = BiomeWeight(home, terrain, decal, centres[i]);
                t.Info($"{decal.Key} at lat {decal.Location.Latitude.ToDegrees():F5} lon {decal.Location.Longitude.ToDegrees():F5}: " +
                       $"bare {bare[i]:F3} m, with the modifiers {height:F3} m, flat {decal.AltitudeOffset.Km:F3} m, biome weight {weight:G4}");
                if (weight < MinBiomeWeight)
                {
                    t.Info($"{decal.Key}: none of its biomes is at its centre, so the game does not apply it there");
                    continue;
                }
                applied++;
                if (Math.Abs(height - bare[i]) > FlatTolerance)
                    changed++;
                t.CheckAbs($"{decal.Key}: the terrain at the decal centre is its flat", height, decal.AltitudeOffset.Km, FlatTolerance);
            }
            if (applied == 0)
                t.Skip($"no flat decal of {home.Id} lies on one of its biomes");
            else
                t.Check("the modifiers change the height at a decal centre from the bare heightmap", changed > 0,
                    $"{changed} of {applied} decal(s) changed it by more than {FlatTolerance} m");

            HeadlessTerrain.Release(home);
            double[] again = centres.Select(c => home.GetTerrainHeightFromDirCcf(c)).ToArray();
            t.Check("Release gives back the bare heights exactly", home.TerrainModifiersRenderData == null && again.SequenceEqual(bare),
                string.Join(", ", again.Select((h, i) => $"{h:R} (bare {bare[i]:R})")));
        }
        finally
        {
            if (runHasData && !HeadlessTerrain.HasCpuModifiers(home))
                HeadlessTerrain.Install(home);
            else if (!runHasData)
                HeadlessTerrain.Release(home);
        }
        t.Check($"{home.Id} is left as the run setting has it", HeadlessTerrain.HasCpuModifiers(home) == runHasData);

        // Universe.OnLoaded runs again for the same system when a test calls it (as control-truth-table
        // does for the rules), and the bodies must keep their data and the bookkeeping its state.
        Universe.OnLoaded();
        t.Check("a second Universe.OnLoaded keeps the run's terrain state", HeadlessTerrain.HasCpuModifiers(home) == runHasData,
            $"data {(HeadlessTerrain.HasCpuModifiers(home) ? "present" : "absent")}");
        string? state = HeadlessTerrain.CheckRunState();
        t.Check("every body is as the run setting has it", state == null, state);
        return t.Finish();
    }

    // The biome weight of a modifier as Celestial.GetTerrainHeightFromDirCcf forms it: the control
    // map weights raised to BlendStrength and normalized, summed over the four biome ids of the
    // point that the modifier's BiomeMask holds.
    private static float BiomeWeight(Celestial body, TerrainReference terrain, ModifierReference modifier, double3 dirCcf)
    {
        int4 ids = body.SampleBiomeIdMap(dirCcf);
        float4 control = body.SampleBiomeControlMap(dirCcf);
        float strength = terrain.BiomeMaterials.BlendStrength;
        float r = float.Pow(control.R, strength);
        float g = float.Pow(control.G, strength);
        float b = float.Pow(control.B, strength);
        float a = float.Pow(control.A, strength);
        float sum = MathF.Max(r + g + b + a, 0.0001f);
        float weight = 0f;
        weight += (modifier.BiomeMask & (uint)(1 << ids.X)) != 0 ? r / sum : 0f;
        weight += (modifier.BiomeMask & (uint)(1 << ids.Y)) != 0 ? g / sum : 0f;
        weight += (modifier.BiomeMask & (uint)(1 << ids.Z)) != 0 ? b / sum : 0f;
        weight += (modifier.BiomeMask & (uint)(1 << ids.W)) != 0 ? a / sum : 0f;
        return weight;
    }
}
