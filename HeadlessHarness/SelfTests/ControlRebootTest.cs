using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// With radiation on, a triggered avionics reboot drops a probe-only vehicle's control for the
// reboot time and releases its Auto attitude, while a crewed pod with the same probe core keeps
// control and its Auto attitude the whole time.
public sealed class ControlRebootTest : IHarnessTest
{
    private const double WatchS = 60.0;
    private const double TolS = 2.0;

    public string Name => "control-reboot";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        try
        {
            using IDisposable rules = TestSupport.UseRules(radiation: true);
            using IDisposable seed = TestSupport.SeedUpsets(7);
            Vehicle? probe = ControlTestVehicles.Spawn(Name, system, "HarnessRebootProbe", 400_000.0, ControlTestVehicles.ProbeCorePart);
            Vehicle? crewed = ControlTestVehicles.Spawn(Name, system, "HarnessRebootCrewed", 430_000.0,
                ControlTestVehicles.PodPart, ControlTestVehicles.ProbeCorePart);
            if (probe == null || crewed == null)
                return 0;
            ControlExpectation expectation = TestSupport.EnsureControllable(crewed);
            HarnessLog.Line($"[{Name}] crewed vehicle: {expectation.Reason}, {expectation.CrewSeated} seated.");
            TestSupport.EnsureControllable(probe);

            driver.Step(1.0);
            ok &= Check("both controllable before the reboot", probe.IsControllable && crewed.IsControllable);
            probe.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;
            crewed.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;
            ok &= Check("one avionics module rebooting on each", TestSupport.TriggerReboot(probe) == 1 && TestSupport.TriggerReboot(crewed) == 1);
            UniverseTime start = driver.Elapsed;

            double? lostAfter = null, backAfter = null;
            bool crewedKept = true, sawRebooting = false;
            while ((driver.Elapsed - start).Seconds() < WatchS)
            {
                driver.Step(1.0);
                double t = (driver.Elapsed - start).Seconds();
                crewedKept &= crewed.IsControllable;
                if (!probe.IsControllable)
                {
                    lostAfter ??= t;
                    sawRebooting |= TestSupport.GetControl(probe) == VehicleControl.ProbeCoreRebooting;
                }
                else if (lostAfter != null)
                {
                    backAfter ??= t;
                }
            }

            ok &= Check($"probe lost control {lostAfter:F0} s after the trigger", lostAfter is double l && l <= TolS);
            ok &= Check("probe read ProbeCoreRebooting", sawRebooting);
            double outage = (backAfter ?? double.NaN) - (lostAfter ?? double.NaN);
            ok &= Check($"probe back after {outage:F0} s (reboot {Avionics.REBOOT_SECONDS:F0} s)",
                Math.Abs(outage - Avionics.REBOOT_SECONDS) <= TolS);
            ok &= Check("probe attitude released to Manual", probe.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Manual);
            ok &= Check("crewed pod kept control throughout", crewedKept);
            ok &= Check("crewed pod kept Auto attitude", crewed.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto);
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

    private bool Check(string what, bool pass)
    {
        HarnessLog.Line($"[{Name}] {what}: {TestSupport.Verdict(pass)}");
        return pass;
    }
}
