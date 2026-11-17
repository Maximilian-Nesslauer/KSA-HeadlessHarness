using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;
using KSA.Aero;

namespace HeadlessHarness.Harness;

// Builds every part template of a mod on its own and checks what a vehicle needs from it: the part
// builds (new Part, Part.CreateOwnTree and PartTree.ReinitializeDerivedValues, the construction the
// game itself checks every part with in PartArchetypes.WarnOnMalformedParts), its static mass is
// finite and positive, it has colliders, and the aero mesh the game builds from those colliders is a
// closed surface. A part that fails one of these loads without an error and only misbehaves in flight
// (a zero mass, no collision, aero forces from an open hull).
public static class PartCheck
{
    // Comma-separated mod ids whose parts to check. Unset or blank: every mod except Core.
    public const string PartModsEnvVar = "KSA_HEADLESS_PART_MODS";

    // "1" writes the aero mesh of every checked part as a Wavefront .obj (AeroDump.WriteMesh) into a
    // folder next to the run log.
    public const string DumpEnvVar = "KSA_HEADLESS_PART_DUMP";

    private static FieldInfo? _allParts;

    public sealed record Result(
        string PartId,
        string ModId,
        double Mass,
        int Colliders,
        int SolidPanels,
        int SheetPanels,
        bool Closed,
        bool Watertight,
        int PinchedEdges,
        IReadOnlyList<string> Problems,
        string? MeshPath)
    {
        public bool Passed => Problems.Count == 0;
    }

    // The mod ids KSA_HEADLESS_PART_MODS names, or null when it is unset (every mod except Core).
    public static IReadOnlyList<string>? ModsFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(PartModsEnvVar);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // The part templates of the named mods, or of every mod except Core when modIds is null, sorted by
    // id. Sub-part templates are left out, because the game places them only inside their part, and
    // so are templates marked Test, which PartArchetypes.WarnOnMalformedParts skips too. The template
    // registry ModLibrary.AllParts is internal, so it is read by name.
    public static IReadOnlyList<PartTemplate> Templates(IEnumerable<string>? modIds = null)
    {
        _allParts ??= AccessTools.Field(typeof(ModLibrary), "AllParts")
            ?? throw new InvalidOperationException("[HeadlessHarness] ModLibrary.AllParts not found - game version may have changed.");
        if (_allParts.GetValue(null) is not SerializedCollection<PartTemplate> registry)
            throw new InvalidOperationException(
                "[HeadlessHarness] ModLibrary.AllParts is no longer a SerializedCollection<PartTemplate> - game version may have changed.");

        HashSet<string>? wanted = modIds != null ? new HashSet<string>(modIds, StringComparer.Ordinal) : null;
        List<PartTemplate> templates = new List<PartTemplate>();
        foreach (PartTemplate template in registry.GetList())
        {
            if (template.IsSubPart || template.Test)
                continue;
            string modId = template.Mod?.Id ?? string.Empty;
            bool match = wanted != null ? wanted.Contains(modId) : !string.Equals(modId, ContentCheck.CoreModId, StringComparison.Ordinal);
            if (match)
                templates.Add(template);
        }
        templates.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return templates;
    }

    // The folder the .obj dumps go to when KSA_HEADLESS_PART_DUMP is "1", otherwise null.
    public static string? DumpDirectoryFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable(DumpEnvVar) != "1")
            return null;
        string dir = Path.GetDirectoryName(HarnessLog.FilePath) ?? HarnessLog.DataDirectory;
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(HarnessLog.FilePath) + "-aero");
    }

    public static IReadOnlyList<Result> CheckMods(IEnumerable<string>? modIds = null, string? dumpDirectory = null)
    {
        List<Result> results = new List<Result>();
        foreach (PartTemplate template in Templates(modIds))
            results.Add(Check(template, dumpDirectory));
        return results;
    }

    // Builds the part in a tree of its own, checks it and disposes the tree again. The part never
    // joins a vehicle or the celestial system, so nothing has to be despawned afterwards; it takes
    // one running id (Universe.GetNextRunningId) per part and sub-part. A build that throws is
    // reported as a problem of that part.
    public static Result Check(PartTemplate template, string? dumpDirectory = null)
    {
        string modId = template.Mod?.Id ?? string.Empty;
        List<string> problems = new List<string>();
        double mass = double.NaN;
        int colliders = 0, solid = 0, sheet = 0, pinched = 0;
        bool closed = false, watertight = false;
        string? meshPath = null;

        PartTree? tree = null;
        try
        {
            Part part = new Part(template.Id, template);
            tree = part.CreateOwnTree();
            // CreateOwnTree registers only the modules of the part and its direct sub-parts; this adds
            // the nested sub-parts and rebuilds the derived data from them.
            tree.ReinitializeDerivedValues();

            mass = tree.StaticMassPropsAsmb.Props.Mass;
            if (!double.IsFinite(mass) || mass <= 0.0)
                problems.Add($"static mass {mass} is not finite and positive");

            // Counted the way AeroMeshSource.Gather collects the aero shapes, from the subtree modules
            // of every part that is not a sub-part.
            foreach (Part treePart in tree.Parts)
            {
                if (!treePart.IsSubPart)
                    colliders += treePart.SubtreeModules.Get<ColliderModule>().Length;
            }
            if (colliders == 0)
                problems.Add("no colliders");

            AeroMesh? mesh = BuildAeroMesh(tree, problems);
            if (mesh != null)
            {
                solid = mesh.SolidPanels.Length;
                sheet = mesh.SheetPanels.Length;
                closed = ClosedSurfaceChecks.IsClosed(mesh.SolidPanels, out pinched);
                watertight = ClosedSurfaceChecks.IsWatertight(mesh.SolidPanels, out _);
                // A part whose colliders all became sheet panels (a fin, a flat panel) has no solid
                // hull to close.
                if (solid > 0 && !closed)
                    problems.Add($"aero mesh is open ({solid} solid panels)");
                else if (solid == 0 && sheet == 0 && colliders > 0)
                    problems.Add("aero mesh is empty although the part has colliders");
                if (dumpDirectory != null)
                    meshPath = DumpMesh(dumpDirectory, template.Id, mesh);
            }
        }
        catch (Exception e)
        {
            problems.Add($"build threw {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            tree?.Dispose();
        }
        return new Result(template.Id, modId, mass, colliders, solid, sheet, closed, watertight, pinched, problems, meshPath);
    }

    // AerodynamicsBuilder.Start with a null pool runs AeroMeshBuildJob.Execute on this thread, so
    // the build is finished when it returns, with no worker pool timing involved. The finished
    // Aerodynamics is handed to the tree (PartTree.ExchangeAerodynamics), so PartTree.Dispose
    // releases its buffers.
    private static AeroMesh? BuildAeroMesh(PartTree tree, List<string> problems)
    {
        using AerodynamicsBuilder builder = new AerodynamicsBuilder();
        AeroMeshSource source = AeroMeshSource.Gather(tree);
        builder.Start(source, null, null, null);
        if (builder.HasFailed || !builder.TryTakeFinished(out Aerodynamics? aerodynamics))
        {
            problems.Add("aero mesh build failed (see the game log for the exception)");
            return null;
        }
        tree.ExchangeAerodynamics(aerodynamics);
        return aerodynamics.Mesh;
    }

    private static string? DumpMesh(string directory, string partId, AeroMesh mesh)
    {
        string path = Path.Combine(directory, string.Join("_", partId.Split(Path.GetInvalidFileNameChars())) + AeroDump.MeshExtension);
        try
        {
            Directory.CreateDirectory(directory);
            AeroDump.WriteMesh(path, mesh);
            return path;
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] could not write the aero mesh of '{partId}' to {path}: {e.Message}");
            return null;
        }
    }
}
