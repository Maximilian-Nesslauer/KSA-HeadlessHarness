using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;
using C = KSA.VehicleControl;

namespace HeadlessHarness.SelfTests;

// Pins the game's control rule: Vehicle.ComputeControl over every pair of pod crew and probe core
// power, against a table written out by hand. A changed rule or a new enum value fails here before
// it shows up as a vehicle that silently stops flying. Also binds the control reflection keys.
public sealed class ControlTruthTableTest : IHarnessTest
{
    private static readonly ProbeCorePower[] Probes =
    {
        ProbeCorePower.NoProbeCore, ProbeCorePower.Failed, ProbeCorePower.SwitchedOff, ProbeCorePower.StormStandby,
        ProbeCorePower.Rebooting, ProbeCorePower.Unpowered, ProbeCorePower.Powered,
    };

    private static readonly CommandPodCrew[] Pods =
    {
        CommandPodCrew.NoCommandPod, CommandPodCrew.Empty, CommandPodCrew.Incapacitated, CommandPodCrew.Able,
    };

    // Rows follow Pods, columns follow Probes. NoCommandPod with NoProbeCore is Controllable because
    // a vehicle without any control module is refused earlier, by Vehicle.HasControlModule.
    private static readonly C[,] Expected =
    {
        { C.Controllable, C.ProbeCoreFailed, C.ProbeCoreOff, C.ProbeCoreStormStandby, C.ProbeCoreRebooting, C.ProbeCoreUnpowered, C.Controllable },
        { C.NoCrew, C.ProbeCoreFailed, C.ProbeCoreOff, C.ProbeCoreStormStandby, C.ProbeCoreRebooting, C.ProbeCoreUnpowered, C.Controllable },
        { C.CrewIncapacitated, C.CrewIncapacitated, C.CrewIncapacitated, C.CrewIncapacitated, C.CrewIncapacitated, C.CrewIncapacitated, C.Controllable },
        { C.Controllable, C.Controllable, C.Controllable, C.Controllable, C.Controllable, C.Controllable, C.Controllable },
    };

    public string Name => "control-truth-table";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        ok &= CheckEnumSize<CommandPodCrew>(Pods.Length);
        ok &= CheckEnumSize<ProbeCorePower>(Probes.Length);
        ok &= CheckEnumSize<VehicleControl>(8);

        int mismatches = 0;
        for (int p = 0; p < Pods.Length; p++)
        {
            for (int q = 0; q < Probes.Length; q++)
            {
                VehicleControl actual = Vehicle.ComputeControl(Pods[p], Probes[q]);
                if (actual != Expected[p, q])
                {
                    mismatches++;
                    HarnessLog.Line($"[{Name}] ComputeControl({Pods[p]}, {Probes[q]}) = {actual}, expected {Expected[p, q]}.");
                }
            }
        }
        ok &= mismatches == 0;
        HarnessLog.Line($"[{Name}] {Pods.Length * Probes.Length} pairs, {mismatches} mismatch(es).");

        GameRules expectedRules = TestSupport.GetRunRules();
        bool radiation = Universe.Rules.Radiation, lifeSupport = Universe.Rules.LifeSupport;
        try
        {
            Universe.Rules.Radiation = !expectedRules.Radiation;
            Universe.Rules.LifeSupport = !expectedRules.LifeSupport;
            Universe.OnLoaded();
            bool restored = Universe.Rules.Radiation == expectedRules.Radiation &&
                            Universe.Rules.LifeSupport == expectedRules.LifeSupport;
            ok &= restored;
            HarnessLog.Line($"[{Name}] system-load callback restores run rules => {TestSupport.Verdict(restored)}");
        }
        finally
        {
            Universe.Rules.Radiation = radiation;
            Universe.Rules.LifeSupport = lifeSupport;
        }

        try
        {
            TestSupport.BindControlKeys();
            UniverseTime? limit = SimDriver.NextGameStepLimit;
            HarnessLog.Line($"[{Name}] reflection keys bound; next game step limit {(limit is UniverseTime t ? t.Seconds().ToString("F1") : "none")}.");
        }
        catch (InvalidOperationException e)
        {
            HarnessLog.Line($"[{Name}] FAIL: {e.Message}");
            ok = false;
        }

        HarnessLog.Line($"[{Name}] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private bool CheckEnumSize<T>(int expected) where T : struct, Enum
    {
        int actual = Enum.GetValues<T>().Length;
        if (actual == expected)
            return true;
        HarnessLog.Line($"[{Name}] FAIL: {typeof(T).Name} has {actual} values, the table knows {expected}: {string.Join(", ", Enum.GetNames<T>())}.");
        return false;
    }
}
