using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The worked example of a MULTI-VEHICLE test: instead of the single KSA_HEADLESS_VEHICLE pin, it asks
// TestSupport.ResolveVehicleSaves which of a candidate set is present (overridable with
// KSA_HEADLESS_VEHICLES, or run-headless.ps1 -Vehicles), then iterates the resolved list. This is the
// shape a calibration sweep copies: resolve once, one SKIP line per save that will not run, a per-save
// log prefix so multiple vehicles stay readable. It is opt-in because a real sweep is expensive; this
// one only spawns each save and reads its mass so the mechanism stays cheap to demonstrate.
public sealed class ExampleMultiVehicleTest : IHarnessTest
{
    private static readonly string[] Candidates = { "Test Vehicle 1", "RCS Test 1" };
    private const double SpawnAltitudeM = 500_000.0;

    public string Name => "example-multi-vehicle";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        IReadOnlyList<string> saves = TestSupport.ResolveVehicleSaves(Candidates);
        if (saves.Count == 0)
        {
            HarnessLog.Line("[example-multi-vehicle] SKIP: none of the candidate saves are present.");
            return 0;
        }
        if (session.System.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[example-multi-vehicle] FAIL: the loaded system has no home body.");
            return 1;
        }

        HarnessLog.Line($"[example-multi-vehicle] resolved {saves.Count} save(s): {string.Join(", ", saves)}");
        CelestialSystem system = session.System;
        SimTime now = Universe.GetElapsedSimTime();
        Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, now);

        int processed = 0;
        foreach (string saveId in saves)
        {
            // Snapshot per iteration so each save's vehicle (and any stage it sheds) is torn down
            // before the next spawns, keeping only one test vehicle live at a time.
            HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
            Vehicle vehicle;
            try
            {
                vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "ExampleMultiVehicle", orbit);
            }
            catch (InvalidOperationException e)
            {
                HarnessLog.Line($"[example-multi-vehicle] SKIP '{saveId}': {e.Message}");
                continue;
            }

            int engineCount = vehicle.Parts.Modules.Get<EngineController>().Length;
            HarnessLog.Line($"[example-multi-vehicle] '{saveId}': mass={vehicle.TotalMass:F0}kg, {engineCount} engine(s)");
            TestSupport.DespawnNewVehicles(system, preexisting);
            processed++;
        }

        HarnessLog.Line($"[example-multi-vehicle] {processed} save(s) processed => {TestSupport.Verdict(processed > 0)}");
        return processed > 0 ? 0 : 1;
    }
}
