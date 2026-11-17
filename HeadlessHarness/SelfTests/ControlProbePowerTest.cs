using System.Reflection;
using HarmonyLib;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// A probe-only vehicle runs its battery flat. Phase 1 steps one second at a time: control must be
// lost within a few steps of the forecast the game made at the start (PowerForecast.Remaining),
// the Auto attitude and burn modes must go to Manual one step after the loss (the release queues
// through the input buffer), and the manual throttle must stay. Phase 2 refills and steps 600 s at
// a time with SimDriver.ClipToGameStepLimit: the steps must land on the game's step limits, so the
// loss is found within a few seconds of the forecast instead of up to 600 s late.
public sealed class ControlProbePowerTest : IHarnessTest
{
    private const float Throttle = 0.5f;
    private const double LossWindowS = 5.0;
    private const double BurnLeadS = 5000.0;
    private const double LargeStepS = 600.0;

    public string Name => "control-probe-power";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        try
        {
            using IDisposable rules = TestSupport.UseRules(radiation: false, lifeSupport: false);
            Vehicle? probe = ControlTestVehicles.Spawn(Name, system, "HarnessProbePower", 400_000.0, ControlTestVehicles.ProbeCorePart);
            if (probe == null)
                return 0;

            TestSupport.SetManualControlInputs(probe, Throttle, engineOn: false);
            driver.Step(1.0);
            bool burnPlanned = PlanBurn(probe, driver);
            probe.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;
            if (burnPlanned)
                probe.FlightComputer.BurnMode = FlightComputerBurnMode.Auto;
            driver.Step(1.0);
            ok &= Check("controllable at the start", probe.IsControllable);
            ok &= Check("Auto attitude holds", probe.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto);
            if (burnPlanned)
                ok &= Check("Auto burn holds before ignition", probe.FlightComputer.BurnMode == FlightComputerBurnMode.Auto);

            ok &= RunFixedSteps(probe, driver, burnPlanned);
            ok &= RunClippedSteps(probe, driver);
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
            TestSupport.ClearAlerts();
            TestSupport.ResetSimulationSpeed();
        }

        HarnessLog.Line($"[{Name}] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private bool RunFixedSteps(Vehicle probe, SimDriver driver, bool burnPlanned)
    {
        TestSupport.RefillBatteries(probe);
        if (TestSupport.ProbeForecast(probe) is not PowerForecast forecast || forecast.Drain != PowerDrain.Draining)
            return Check("probe core drains its battery", false);
        double remaining = forecast.Remaining.Seconds();
        // Vehicle.PrepareWorker clamps the manual throttle to PartTree.EngineThrottleMin every step,
        // which is 1 on a vehicle without engines, so the value to keep is the one after that clamp.
        float throttleBefore = ReadManualThrottle(probe);
        UniverseTime start = driver.Elapsed;
        UniverseTime? lost = StepUntilLoss(probe, driver, 1.0, remaining + 60.0);
        if (lost is not UniverseTime lostAt)
            return Check($"control lost within {remaining + 60.0:F0} s", false);

        double after = (lostAt - start).Seconds();
        bool ok = Check($"1 s steps: control lost {after:F0} s after the refill, forecast {remaining:F1} s",
            after >= remaining && after <= remaining + LossWindowS);
        ok &= Check("loss reads ProbeCoreUnpowered", TestSupport.GetControl(probe) == VehicleControl.ProbeCoreUnpowered);
        ok &= Check("Auto attitude still set on the loss step", probe.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto);
        driver.Step(1.0);
        ok &= Check("attitude released to Manual one step later", probe.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Manual);
        if (burnPlanned)
            ok &= Check("burn released to Manual one step later", probe.FlightComputer.BurnMode == FlightComputerBurnMode.Manual);
        float throttle = ReadManualThrottle(probe);
        ok &= Check($"manual throttle stays ({throttleBefore:F2} -> {throttle:F2})", throttle == throttleBefore);
        return ok;
    }

    private bool RunClippedSteps(Vehicle probe, SimDriver driver)
    {
        TestSupport.RefillBatteries(probe);
        driver.Step(1.0);
        bool ok = Check("refill restores control", probe.IsControllable);
        double remaining = TestSupport.ProbeForecast(probe)!.Value.Remaining.Seconds();
        UniverseTime start = driver.Elapsed;

        driver.ClipToGameStepLimit = true;
        int clipped = 0;
        UniverseTime? lostAt = null;
        for (int i = 0; i < 20 && lostAt == null; i++)
        {
            UniverseTime? limit = SimDriver.NextGameStepLimit;
            driver.Step(LargeStepS);
            if (driver.LastStepClipped)
                clipped++;
            HarnessLog.Line($"[{Name}] clipped run: t={(driver.Elapsed - start).Seconds():F1} s, limit was " +
                            $"{(limit is UniverseTime l ? (l - start).Seconds().ToString("F1") : "none")}, clipped={driver.LastStepClipped}, " +
                            $"charge={TestSupport.GetBatteryCharge(probe):F1} J, control={TestSupport.GetControl(probe)}");
            if (!probe.IsControllable)
                lostAt = driver.Elapsed;
        }
        driver.ClipToGameStepLimit = false;

        if (lostAt is not UniverseTime lost)
            return Check("clipped run loses control", false);
        double after = (lost - start).Seconds();
        ok &= Check($"{LargeStepS:F0} s steps clipped to the game's limits: loss {after:F1} s after start, forecast {remaining:F1} s, {clipped} clipped step(s)",
            clipped > 0 && after >= remaining && after <= remaining + LossWindowS);
        return ok;
    }

    private static UniverseTime? StepUntilLoss(Vehicle probe, SimDriver driver, double dt, double maxSeconds)
    {
        UniverseTime end = driver.Elapsed + maxSeconds;
        while (driver.Elapsed < end)
        {
            driver.Step(dt);
            if (!probe.IsControllable)
                return driver.Elapsed;
        }
        return null;
    }

    // A burn far ahead, queued the way the game's burn context menu adds one, so the flight
    // computer holds an Auto burn mode until the loss of control (without a burn it falls back to
    // Manual on its own, FlightComputer.UpdateBurnTarget).
    private bool PlanBurn(Vehicle probe, SimDriver driver)
    {
        UniverseTime time = driver.Elapsed + BurnLeadS;
        PatchedConic? patch = probe.FlightPlan.TryFindPatch(time);
        if (patch == null)
        {
            HarnessLog.Line($"[{Name}] no flight plan patch at the burn time; the burn mode is not checked.");
            return false;
        }
        Burn burn = Burn.Create(patch.Orbit.GetPointAt(time), time.Seconds(), new Brutal.Numerics.double3(1.0, 0.0, 0.0), patch, probe);
        InputEvents.BurnUpdateBuffer.Add(new InputEvents.BurnUpdateData { FlightComputer = probe.FlightComputer, Burn = burn, AddBurn = true });
        driver.Step(1.0);
        bool loaded = probe.FlightComputer.Burn != null;
        HarnessLog.Line($"[{Name}] burn planned {BurnLeadS:F0} s ahead: {(loaded ? "loaded" : "not loaded, the burn mode is not checked")}.");
        return loaded;
    }

    private static float ReadManualThrottle(Vehicle vehicle)
    {
        FieldInfo field = AccessTools.Field(typeof(Vehicle), "_manualControlInputs");
        return ((ManualControlInputs)field.GetValue(vehicle)!).EngineThrottle;
    }

    private bool Check(string what, bool pass)
    {
        HarnessLog.Line($"[{Name}] {what}: {TestSupport.Verdict(pass)}");
        return pass;
    }
}
