using System.Reflection;
using HarmonyLib;
using KSA;

namespace HeadlessHarness.Harness;

public sealed partial class HeadlessSession
{
    internal static bool RunRulesKeepSettings => (_rulesRequest ?? RulesRequest.AllOff).KeepSettings;

    internal static bool RunRadiation => (_rulesRequest ?? RulesRequest.AllOff).Radiation;

    internal static bool RunLifeSupport => (_rulesRequest ?? RulesRequest.AllOff).LifeSupport;

    static partial void InstallControlPatches(Harmony harmony)
    {
        TestSupport.InitializeUpsetSeedFromEnvironment();

        // The getter declared on Vehicle. KittenEva overrides IsControllable, so an EVA keeps its own
        // answer, and TestSupport.ForceControllable refuses one.
        MethodInfo isControllable = AccessTools.DeclaredPropertyGetter(typeof(Vehicle), nameof(Vehicle.IsControllable))
            ?? throw Drift("Vehicle.IsControllable getter");
        harmony.Patch(isControllable,
            postfix: new HarmonyMethod(typeof(HeadlessSession), nameof(ForceControllablePostfix)));

        // Vehicle.RollUpset creates its Random on first use with no seed, so a radiation run never
        // repeats. The prefix places a seeded one first.
        MethodInfo rollUpset = AccessTools.Method(typeof(Vehicle), "RollUpset")
            ?? throw Drift("Vehicle.RollUpset");
        harmony.Patch(rollUpset,
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(SeedUpsetPrefix)));
    }

    private static void ForceControllablePostfix(Vehicle __instance, ref bool __result)
    {
        if (!__result && TestSupport.IsForcedControllable(__instance))
            __result = true;
    }

    private static void SeedUpsetPrefix(Vehicle __instance) => TestSupport.SeedUpsetRandom(__instance);
}
