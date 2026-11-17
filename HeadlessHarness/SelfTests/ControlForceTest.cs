using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Two uncrewed pods with Auto attitude lose control on their first step (NoCrew). The free one is
// released to Manual one step later, as Vehicle.ReleaseFlightComputer does; the forced one stays
// controllable and on Auto for as long as the ForceControllable scope holds, and is released when
// the scope ends.
public sealed class ControlForceTest : IHarnessTest
{
    private const int ForcedSteps = 5;

    public string Name => "control-force";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        try
        {
            Vehicle? forced = ControlTestVehicles.Spawn(Name, system, "HarnessControlForced", 400_000.0, ControlTestVehicles.PodPart);
            Vehicle? free = ControlTestVehicles.Spawn(Name, system, "HarnessControlFree", 430_000.0, ControlTestVehicles.PodPart);
            if (forced == null || free == null)
                return 0;
            forced.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;
            free.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto;

            using (TestSupport.ForceControllable(forced))
            {
                for (int i = 0; i < ForcedSteps; i++)
                {
                    driver.Step(1.0);
                    ok &= Check($"step {i + 1}: forced pod controllable", TestSupport.IsControllable(forced));
                }
                ok &= Check("the game's own verdict on the forced pod is NoCrew", TestSupport.GetControl(forced) == VehicleControl.NoCrew);
                ok &= Check("forced pod kept Auto attitude", forced.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto);
                ok &= Check("free pod not controllable", !TestSupport.IsControllable(free));
                ok &= Check("free pod released to Manual", free.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Manual);
            }

            ok &= Check("forced pod not controllable after dispose", !TestSupport.IsControllable(forced));
            driver.Step(1.0);
            ok &= Check("forced pod released to Manual one step after dispose", forced.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Manual);
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
