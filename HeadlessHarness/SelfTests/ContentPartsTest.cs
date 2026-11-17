using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Runs PartCheck on every part template of the mods KSA_HEADLESS_PART_MODS names (unset: every mod
// except Core) and fails on any part that does not build, has no positive static mass, no colliders,
// or an open aero mesh. One CSV row per part. Opt-in, because it builds an aero mesh for every part.
public sealed class ContentPartsTest : IHarnessTest
{
    public string Name => "content-parts";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        IReadOnlyList<string>? mods = PartCheck.ModsFromEnvironment();
        string scope = mods != null ? string.Join(", ", mods) : $"every mod except {ContentCheck.CoreModId}";
        IReadOnlyList<PartTemplate> templates = PartCheck.Templates(mods);
        if (templates.Count == 0)
        {
            HarnessLog.Line($"[content-parts] SKIP: no part templates in {scope} (set {PartCheck.PartModsEnvVar} to name the mods).");
            return 0;
        }

        string? dump = PartCheck.DumpDirectoryFromEnvironment();
        HarnessLog.Line($"[content-parts] checking {templates.Count} part(s) of {scope}" +
                        (dump != null ? $", aero meshes to {dump}." : "."));
        HarnessData data = HarnessData.Create("content-parts",
            "part,mod,mass_kg,colliders,solid_panels,sheet_panels,closed,watertight,pinched_edges,problems");

        int failed = 0;
        foreach (PartTemplate template in templates)
        {
            PartCheck.Result result = PartCheck.Check(template, dump);
            data.AppendRow(result.PartId, result.ModId, result.Mass, result.Colliders, result.SolidPanels, result.SheetPanels,
                result.Closed, result.Watertight, result.PinchedEdges, string.Join("; ", result.Problems));
            if (!result.Passed)
            {
                failed++;
                HarnessLog.Line($"[content-parts]   FAIL '{result.PartId}' ({result.ModId}): {string.Join("; ", result.Problems)}");
            }
        }

        bool ok = failed == 0;
        HarnessLog.Line($"[content-parts] {TestSupport.Verdict(ok)} ({templates.Count - failed}/{templates.Count} part(s) passed).");
        return ok ? 0 : 1;
    }
}
