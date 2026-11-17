using System.Reflection;
using HarmonyLib;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Checks the control helpers of TestSupport against what the game does a step later: the rules
// scope, battery writes before and between steps, the probe forecast, the power switch, failed
// avionics and a reboot, the control prediction of EnsureControllable, seeded upsets, missions and
// the hygiene helpers. Two single-part vehicles and a few one-second steps.
public sealed class ControlHelpersTest : IHarnessTest
{
    private const double RelTol = 1e-3;
    private const int Seed = 1234;

    public string Name => "control-helpers";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        GameRules before = new GameRules { Radiation = Universe.Rules.Radiation, LifeSupport = Universe.Rules.LifeSupport };
        bool ok = true;
        try
        {
            ok &= CheckRulesScope(before);

            Vehicle? probe = ControlTestVehicles.Spawn(Name, system, "HarnessControlProbe", 400_000.0, ControlTestVehicles.ProbeCorePart);
            Vehicle? pod = ControlTestVehicles.Spawn(Name, system, "HarnessControlPod", 430_000.0, ControlTestVehicles.PodPart);
            if (probe == null || pod == null)
                return 0;

            ok &= CheckBatteryWrites(probe, driver);
            ok &= CheckPowerSwitch(probe, driver);
            ok &= CheckEnsureControllable(pod, driver);
            using (TestSupport.UseRules(radiation: true))
            {
                ok &= CheckAvionics(probe, driver);
                ok &= CheckSeededUpsets(probe, driver);
            }
            ok &= CheckMission(probe, driver);
            ok &= CheckHygiene();
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
            Universe.Rules.Radiation = before.Radiation;
            Universe.Rules.LifeSupport = before.LifeSupport;
            TestSupport.ClearAlerts();
            TestSupport.ResetSimulationSpeed();
        }

        HarnessLog.Line($"[{Name}] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private bool CheckRulesScope(GameRules before)
    {
        bool ok = true;
        using (TestSupport.UseRules(radiation: !before.Radiation))
        {
            ok &= Check("rules scope sets radiation", Universe.Rules.Radiation == !before.Radiation);
            ok &= Check("rules scope leaves life support", Universe.Rules.LifeSupport == before.LifeSupport);
        }
        ok &= Check("rules scope restores", Universe.Rules.Radiation == before.Radiation && Universe.Rules.LifeSupport == before.LifeSupport);
        GameRules run = TestSupport.GetRunRules();
        HarnessLog.Line($"[{Name}] run rules: radiation={run.Radiation}, life support={run.LifeSupport}.");
        return ok;
    }

    // A write before the first step and one between steps must both be what the next step starts
    // from, so the charge after a step is the written charge less draw times dt.
    private bool CheckBatteryWrites(Vehicle probe, SimDriver driver)
    {
        bool ok = true;
        const double written = 500.0;
        const double draw = 100.0;
        ok &= Check("one probe core draw set", TestSupport.SetProbeDraw(probe, draw) == 1);
        SetCharge(probe, written);
        driver.Step(1.0);
        double afterFirst = TestSupport.GetBatteryCharge(probe);
        ok &= Check($"write before the first step survives ({afterFirst:F1} J, expect {written - draw:F1})", Near(afterFirst, written - draw));

        int refilled = TestSupport.RefillBatteries(probe);
        double full = TestSupport.GetBatteryCharge(probe);
        PowerForecast? forecast = TestSupport.ProbeForecast(probe);
        driver.Step(1.0);
        double afterRefill = TestSupport.GetBatteryCharge(probe);
        ok &= Check($"refill between steps survives ({afterRefill:F1} J from {full:F1} J, {refilled} battery)", refilled == 1 && Near(afterRefill, full - draw));
        ok &= Check($"forecast {forecast?.Remaining.Seconds():F1} s = charge / draw",
            forecast is PowerForecast f && f.Drain == PowerDrain.Draining && Near(f.Remaining.Seconds(), full / draw));
        TestSupport.SetProbeDraw(probe, 3.0);
        TestSupport.RefillBatteries(probe);
        return ok;
    }

    private bool CheckPowerSwitch(Vehicle probe, SimDriver driver)
    {
        bool ok = true;
        TestSupport.SetProbeMode(probe, PowerSwitchMode.Off);
        ok &= Check("prediction for a switched off core", TestSupport.PredictControl(probe).Predicted == VehicleControl.ProbeCoreOff);
        driver.Step(1.0);
        ok &= Check("switched off core: ProbeCoreOff", TestSupport.GetControl(probe) == VehicleControl.ProbeCoreOff && !probe.IsControllable);
        TestSupport.SetProbeMode(probe, PowerSwitchMode.Auto);
        ok &= Check("prediction for a powered core", TestSupport.PredictControl(probe).Expected);
        driver.Step(1.0);
        ok &= Check("core back on: Controllable", TestSupport.GetControl(probe) == VehicleControl.Controllable && probe.IsControllable);
        return ok;
    }

    private bool CheckEnsureControllable(Vehicle pod, SimDriver driver)
    {
        bool ok = true;
        ControlExpectation empty = TestSupport.PredictControl(pod);
        ok &= Check($"uncrewed pod predicted NoCrew ({empty.Reason})", empty.Predicted == VehicleControl.NoCrew && !empty.Expected);
        driver.Step(1.0);
        ok &= Check("uncrewed pod: NoCrew", TestSupport.GetControl(pod) == VehicleControl.NoCrew);
        ControlExpectation ensured = TestSupport.EnsureControllable(pod);
        ok &= Check($"EnsureControllable seats crew ({ensured.Reason}, {ensured.CrewSeated} seated)", ensured.Expected && ensured.CrewSeated == 1);
        driver.Step(1.0);
        ok &= Check("crewed pod: Controllable", TestSupport.GetControl(pod) == VehicleControl.Controllable && pod.IsControllable);
        return ok;
    }

    private bool CheckAvionics(Vehicle probe, SimDriver driver)
    {
        bool ok = true;
        ok &= Check("one avionics module failed", TestSupport.FailAvionics(probe) == 1);
        ok &= Check("prediction for failed avionics", TestSupport.PredictControl(probe).Predicted == VehicleControl.ProbeCoreFailed);
        driver.Step(1.0);
        ok &= Check("failed avionics: ProbeCoreFailed", TestSupport.GetControl(probe) == VehicleControl.ProbeCoreFailed);
        TestSupport.SetAvionicsDose(probe, 0.0);
        driver.Step(1.0);
        ok &= Check("dose cleared: Controllable", TestSupport.GetControl(probe) == VehicleControl.Controllable);
        TestSupport.TriggerReboot(probe, 5.0);
        ok &= Check("prediction for a reboot", TestSupport.PredictControl(probe).Predicted == VehicleControl.ProbeCoreRebooting);
        driver.Step(1.0);
        ok &= Check("reboot: ProbeCoreRebooting", TestSupport.GetControl(probe) == VehicleControl.ProbeCoreRebooting);
        TestSupport.SetProbeMode(probe, PowerSwitchMode.Off);
        ok &= Check("switch off takes priority over reboot", TestSupport.PredictControl(probe).Predicted == VehicleControl.ProbeCoreOff);
        driver.Step(1.0);
        ok &= Check("switched-off reboot: ProbeCoreOff", TestSupport.GetControl(probe) == VehicleControl.ProbeCoreOff);
        TestSupport.SetProbeMode(probe, PowerSwitchMode.On);
        TestSupport.TriggerReboot(probe, 0.0);
        driver.Step(1.0);
        ok &= Check("reboot over: Controllable", TestSupport.GetControl(probe) == VehicleControl.Controllable);
        return ok;
    }

    // The prefix seeds the vehicle's Random from its id and the seed before Vehicle.RollUpset
    // draws from it. The game draws once per avionics module when the core is on, so the vehicle's
    // next value is the first or the second of a Random seeded the same way.
    private bool CheckSeededUpsets(Vehicle probe, SimDriver driver)
    {
        int? previous = TestSupport.UpsetSeed;
        bool ok = true;
        FieldInfo field = AccessTools.Field(typeof(Vehicle), "_upsetRandom");
        object? original = field.GetValue(probe);
        using (TestSupport.SeedUpsets(Seed))
        {
            driver.Step(1.0);
            Random? random = (Random?)field.GetValue(probe);
            Random reference = new Random(TestSupport.UpsetSeedFor(probe.Id, Seed));
            double first = reference.NextDouble();
            double second = reference.NextDouble();
            double seen = random?.NextDouble() ?? double.NaN;
            ok &= Check($"upset Random seeded from id and seed {Seed}", seen == first || seen == second);
            using (TestSupport.SeedUpsets(Seed + 1))
                driver.Step(1.0);
            ok &= Check("nested scope restores the outer random stream", ReferenceEquals(field.GetValue(probe), random));
        }
        ok &= Check("seed scope restores", TestSupport.UpsetSeed == previous);
        ok &= Check("seed scope restores the previous random stream", ReferenceEquals(field.GetValue(probe), original));
        return ok;
    }

    private bool CheckMission(Vehicle probe, SimDriver driver)
    {
        MissionPlan plan = new MissionPlan { Custom = true, CustomLength = new UniverseTime(3600.0) };
        TestSupport.SetMission(probe, plan);
        driver.Step(1.0);
        bool ok = Check("mission kept across a step", ReferenceEquals(TestSupport.GetMission(probe), plan));
        TestSupport.SetMission(probe, null);
        ok &= Check("mission removed", TestSupport.GetMission(probe) == null);
        return ok;
    }

    private bool CheckHygiene()
    {
        Universe.SetSimulationSpeed(4.0, alert: false);
        TestSupport.ResetSimulationSpeed();
        bool ok = Check("simulation speed reset to 1", Universe.GetSimulationSpeed() == 1.0);
        TestSupport.ClearAlerts();
        return ok;
    }

    private static void SetCharge(Vehicle vehicle, double joules)
    {
        PartTree tree = vehicle.Parts;
        foreach (Battery battery in tree.Modules.Get<Battery>())
            tree.Batteries.GetModuleAndAllMutableStatesForInitialization(battery).State.Charge = new Joules((float)joules);
    }

    private static bool Near(double actual, double expected) =>
        Math.Abs(actual - expected) <= RelTol * Math.Max(1.0, Math.Abs(expected));

    private bool Check(string what, bool pass)
    {
        HarnessLog.Line($"[{Name}] {what}: {TestSupport.Verdict(pass)}");
        return pass;
    }
}
