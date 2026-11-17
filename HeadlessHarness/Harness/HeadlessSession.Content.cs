using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Makes the game's own save and load of a universe run headless, so a test can write the running
// universe to XML and read it back through the real code path (UniverseData.Create,
// Universe.DeserializeSave), as the save and load menus do.
//
// Three render couplings block that path. CelestialSystem.SerializeSave and
// CelestialSystem.DeserializeSave read Program.GetPlanetRenderer().GroundClutterRenderer.
// Universe.DeserializeSave reads the same and also clears the particle emitters, plume trails and
// explosion volumes through Program.Instance, which is null headless, before it sets any state. Each
// call site is rewritten in place; the count of rewritten sites is checked, so a changed method
// aborts the bring-up instead of silently keeping a crash.
//
// Universe.DeserializeSave also replaces Universe.Rules with the rules stored in the save. A postfix
// applies the run's rules again, so a loaded save cannot turn radiation or life support on behind
// the run's back.
public sealed partial class HeadlessSession
{
    static partial void InstallContentPatches(Harmony harmony)
    {
        HarmonyMethod clutter = new HarmonyMethod(typeof(HeadlessSession), nameof(SkipGroundClutterWithoutRenderer));
        harmony.Patch(
            AccessTools.Method(typeof(CelestialSystem), nameof(CelestialSystem.SerializeSave), Type.EmptyTypes)
                ?? throw Drift("CelestialSystem.SerializeSave"),
            transpiler: clutter);
        harmony.Patch(
            AccessTools.Method(typeof(CelestialSystem), nameof(CelestialSystem.DeserializeSave),
                new[] { typeof(CelestialSystemData), typeof(ISet<string>) })
                ?? throw Drift("CelestialSystem.DeserializeSave"),
            transpiler: clutter);

        MethodInfo universeLoad = AccessTools.Method(typeof(Universe), nameof(Universe.DeserializeSave), new[] { typeof(UniverseData) })
            ?? throw Drift("Universe.DeserializeSave(UniverseData)");
        harmony.Patch(universeLoad,
            transpiler: clutter,
            postfix: new HarmonyMethod(typeof(HeadlessSession), nameof(ReapplyRulesAfterUniverseLoad)));
        harmony.Patch(universeLoad,
            transpiler: new HarmonyMethod(typeof(HeadlessSession), nameof(SkipProgramStatementsWithoutProgram)));
    }

    // Each of the three methods reads the ground clutter renderer exactly once, as
    // Program.GetPlanetRenderer().GroundClutterRenderer?.<call>, and the null-conditional call already
    // skips a null clutter renderer. The pair of calls is replaced by one static call that answers
    // null without a Program. The Harmony prefix on Program.GetPlanetRenderer does not cover these
    // sites: the method only returns Program.Instance._planetRenderer, so the JIT inlines its original
    // body into the rewritten method, past the prefix, and that dereferences the null Program.Instance.
    private static IEnumerable<CodeInstruction> SkipGroundClutterWithoutRenderer(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo planetRenderer = AccessTools.Method(typeof(Program), nameof(Program.GetPlanetRenderer))
            ?? throw Drift("Program.GetPlanetRenderer");
        MethodInfo getter = AccessTools.PropertyGetter(typeof(PlanetRenderer), nameof(PlanetRenderer.GroundClutterRenderer))
            ?? throw Drift("PlanetRenderer.GroundClutterRenderer");
        MethodInfo replacement = AccessTools.Method(typeof(HeadlessSession), nameof(GroundClutterRendererIfProgram));
        List<CodeInstruction> code = new List<CodeInstruction>(instructions);
        int replaced = 0;
        for (int i = 0; i < code.Count - 1; i++)
        {
            if (!code[i].Calls(planetRenderer) || !code[i + 1].Calls(getter) || code[i + 1].labels.Count > 0)
                continue;
            code[i] = new CodeInstruction(OpCodes.Call, replacement) { labels = code[i].labels, blocks = code[i].blocks };
            code.RemoveAt(i + 1);
            replaced++;
        }
        if (replaced != 1)
            throw new InvalidOperationException(
                $"[HeadlessHarness] expected one Program.GetPlanetRenderer().GroundClutterRenderer read in " +
                $"{original.DeclaringType?.Name}.{original.Name}, found {replaced} - game version may have changed.");
        return code;
    }

    private static GroundClutterRenderer? GroundClutterRendererIfProgram() =>
        Program.Instance == null ? null : Program.GetPlanetRenderer()?.GroundClutterRenderer;

    // Universe.DeserializeSave starts with three statements on Program.Instance: it resets the
    // particle emitter pool and clears the plume trails and the explosion volumes. Each statement
    // starts with the Program.Instance getter and ends with a call that returns nothing, with an empty
    // evaluation stack before and after. The rewrite inserts "dup; brtrue run; pop; br after" behind
    // the getter, so the statement runs only when there is a Program. The game holds no other state
    // in those three objects that a headless load needs.
    private static IEnumerable<CodeInstruction> SkipProgramStatementsWithoutProgram(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        const int expected = 3;
        MethodInfo getInstance = AccessTools.PropertyGetter(typeof(Program), nameof(Program.Instance))
            ?? throw Drift("Program.Instance");
        List<CodeInstruction> code = new List<CodeInstruction>(instructions);

        int found = 0;
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.Calls(getInstance))
                found++;
        }
        if (found != expected)
            throw new InvalidOperationException(
                $"[HeadlessHarness] expected {expected} Program.Instance reads in Universe.DeserializeSave, found {found} - " +
                "game version may have changed.");

        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(getInstance))
                continue;
            int end = FindStatementEnd(code, i + 1);
            Label run = generator.DefineLabel();
            Label after = generator.DefineLabel();
            code[i + 1].labels.Add(run);
            code[end + 1].labels.Add(after);
            code.InsertRange(i + 1, new[]
            {
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Brtrue, run),
                new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Br, after),
            });
            i = end + 4;
        }
        return code;
    }

    // The statement ends at the first call that returns nothing. Every instruction up to it must be
    // straight-line code that no branch targets, or the inserted branch would change other flow.
    private static int FindStatementEnd(List<CodeInstruction> code, int start)
    {
        for (int k = start; k < code.Count - 1; k++)
        {
            CodeInstruction instruction = code[k];
            if (instruction.labels.Count > 0 || instruction.blocks.Count > 0 || instruction.Branches(out _)
                || instruction.opcode == OpCodes.Ret || instruction.opcode == OpCodes.Throw)
                break;
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo method && method.ReturnType == typeof(void))
                return k;
        }
        throw new InvalidOperationException(
            "[HeadlessHarness] a Program.Instance statement in Universe.DeserializeSave is no longer a plain call chain - " +
            "game version may have changed.");
    }

    private static void ReapplyRulesAfterUniverseLoad() => ApplyRules("universe load");
}
