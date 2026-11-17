using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The worked example of reading engines and aerodynamics without game internals: the rated and live
// rocket readback of TestSupport (MaxThrust, VacuumIsp, IsFed, MassFlow, Throttle) and an angle of
// attack sweep through AeroProbe written to a CSV with HarnessData. The probe works on a private
// copy of the flow state, so a sweep like this can run between the steps of a flight without
// changing it. Opt-in, because it only demonstrates the calls.
public sealed class ExampleAeroAndEngineTest : IHarnessTest
{
    private const string SaveId = "Test Vehicle 1";
    private const double SpawnAltitudeM = 500_000.0;
    private const double SweepAltitudeM = 10_000.0;
    private const double SweepMach = 2.0;

    public string Name => "example-aero-and-engine";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        if (TestSupport.ResolveVehicleSaves(SaveId).Count == 0)
        {
            HarnessLog.Line($"[example-aero-and-engine] SKIP: no '{SaveId}' save.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home || home.GetPhysicalAtmosphere() == null)
        {
            HarnessLog.Line("[example-aero-and-engine] SKIP: the home body has no atmosphere.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        try
        {
            Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, Universe.GetElapsedTime());
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(SaveId, system, home, "ExampleAeroAndEngine", orbit);
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
            // One step builds the derived data, which publishes the aerodynamic mesh the probe reads.
            session.CreateDriver().Step(1.0);

            foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
            {
                HarnessLog.Line($"[example-aero-and-engine] engine '{engine.Parent.Id}': vac {TestSupport.MaxThrust(engine) / 1000:F1}kN " +
                                $"Isp {TestSupport.VacuumIsp(engine):F1}s, SL {TestSupport.MaxThrust(engine, 101325.0) / 1000:F1}kN " +
                                $"Isp {TestSupport.Isp(engine, 101325.0):F1}s, fed={TestSupport.IsFed(vehicle, engine)} " +
                                $"throttle={TestSupport.Throttle(vehicle, engine):F2} flow={TestSupport.MassFlow(vehicle, engine):F3}kg/s");
            }

            HarnessData data = HarnessData.Create("aero-sweep", "aoa_deg,mach,q_pa,drag_n,lift_n,side_n,pitch_nm");
            for (int aoa = 0; aoa <= 20; aoa += 5)
            {
                AeroLoads loads = AeroProbe.MeasureAtMach(vehicle, home, SweepAltitudeM, SweepMach, aoa * Math.PI / 180.0, 0.0);
                data.AppendRow(aoa, loads.Mach, loads.DynamicPressure, loads.Drag, loads.Lift, loads.SideForce, loads.MomentBody.Y);
            }
            HarnessLog.Line($"[example-aero-and-engine] wrote the sweep to {data.FilePath} => PASS");
            return 0;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }
}
