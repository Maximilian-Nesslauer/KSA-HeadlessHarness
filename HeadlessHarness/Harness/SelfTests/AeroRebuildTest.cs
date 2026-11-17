using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Checks that the aerodynamic mesh builds are settled after every SimDriver step, and that they are
// reproducible: spawn Test Vehicle 1 in vacuum, run its whole sequence list with the throttle closed
// (every split starts a rebuild on the worker pool), and after each step require that no part tree
// is still rebuilding (PartTree.IsRebuildingAerodynamics) or has a failed build
// (PartTree.HasAerodynamicsBuildFailed). The run is done twice; both must end with the same vehicles
// and bit-identical panel widths (AeroMesh.PanelWidth), which AerodynamicsBuilder.FindReusableWidth
// would make depend on thread timing if a build were left running across a step.
// Skips when the save is missing.
public sealed class AeroRebuildTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;
    private const int MaxStagings = 8;
    private const int SettleSteps = 2;

    public string Name => "aero-rebuild";

    public int Run(HeadlessSession session)
    {
        if (!PropulsionTestVehicles.SaveExists())
        {
            HarnessLog.Line($"[aero-rebuild] SKIP: no '{PropulsionTestVehicles.SaveId}' save in {VehicleSaves.SaveFolderPath}.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[aero-rebuild] SKIP: the loaded system has no home body.");
            return 0;
        }

        bool ok = true;
        List<float>? first = null;
        for (int run = 0; run < 2; run++)
        {
            List<float> widths = new();
            ok &= StageOnce(session, system, home, $"HarnessAeroRebuild{run}", widths);
            HarnessLog.Line($"[aero-rebuild] run {run + 1}: {widths.Count} vehicle(s), panel widths [{string.Join(", ", widths.Select(w => w.ToString("G9")))}]");
            if (first == null)
            {
                first = widths;
                continue;
            }
            bool same = first.Count == widths.Count
                        && first.Zip(widths).All(p => BitConverter.SingleToInt32Bits(p.First) == BitConverter.SingleToInt32Bits(p.Second));
            HarnessLog.Line($"[aero-rebuild] both runs give the same panel widths => {TestSupport.Verdict(same)}");
            ok &= same;
        }

        HarnessLog.Line($"[aero-rebuild] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool StageOnce(HeadlessSession session, CelestialSystem system, IParentBody home, string id, List<float> widths)
    {
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool ok = true;
        try
        {
            Vehicle vehicle = PropulsionTestVehicles.SpawnInOrbit(system, home, id, SpawnAltitudeM);
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
            SimDriver driver = session.CreateDriver();
            int step = 0;
            ok &= StepAndCheck(driver, system, preexisting, ref step);

            SequenceList sequences = vehicle.Parts.SequenceList;
            for (int stagings = 0; stagings < MaxStagings && sequences.GetNextSequenceNumber() != -1; stagings++)
            {
                sequences.ActivateNextSequence(vehicle);
                ok &= StepAndCheck(driver, system, preexisting, ref step);
            }
            for (int i = 0; i < SettleSteps; i++)
                ok &= StepAndCheck(driver, system, preexisting, ref step);

            for (int i = 0; i < system.Count; i++)
            {
                if (system.GetIndex(i) is Vehicle v && !preexisting.Contains(v.Id))
                    widths.Add(v.Parts.Aerodynamics?.Mesh?.PanelWidth ?? float.NaN);
            }
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
        return ok;
    }

    private static bool StepAndCheck(SimDriver driver, CelestialSystem system, HashSet<string> preexisting, ref int step)
    {
        driver.Step(1.0);
        step++;
        bool ok = true;
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Vehicle v || preexisting.Contains(v.Id))
                continue;
            if (v.Parts.IsRebuildingAerodynamics || v.Parts.HasAerodynamicsBuildFailed)
            {
                HarnessLog.Line($"[aero-rebuild] FAIL: after step {step} '{v.Id}' rebuilding={v.Parts.IsRebuildingAerodynamics} " +
                                $"failed={v.Parts.HasAerodynamicsBuildFailed}.");
                ok = false;
            }
        }
        return ok;
    }
}
