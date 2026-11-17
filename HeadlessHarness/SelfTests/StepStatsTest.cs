using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Collects step statistics while the KSA_HEADLESS_VEHICLE save flies off rails, and checks what the
// game guarantees about them: one row per step for the vehicle's bubble, at least one substep per
// step, every substep attributed to exactly one limit, and no substep longer than the step. Then
// checks that ResetStepStats and CollectStepStats = false stop the collection, and writes the CSV.
public sealed class StepStatsTest : IHarnessTest
{
    private const int BurnSeconds = 3;
    private const int CoastSeconds = 3;
    private const double StepSeconds = 1.0;

    public string Name => "step-stats";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool forceOffRails = PhysicsBubble._forceOffRails;
        try
        {
            Vehicle? vehicle = ContentFixture.Spawn(session, Name, "HarnessStepStats", out int failure);
            if (vehicle == null)
                return failure;

            PhysicsBubble._forceOffRails = true;
            SimDriver driver = session.CreateDriver();
            driver.CollectStepStats = true;
            ContentFixture.Fly(vehicle, driver, BurnSeconds, CoastSeconds);
            int steps = BurnSeconds + CoastSeconds;

            bool ok = CheckRows(driver.StepStats, vehicle.Id, steps);
            string csv = driver.WriteStepStats(Name);
            foreach (StepStatsTotal total in driver.SummarizeStepStats())
                HarnessLog.Line($"[{Name}]   bubble '{total.LeaderId}': {total.Steps} step(s), {total.Substeps} substep(s), " +
                                $"{total.FlightComputerRuns} flight computer run(s), step {total.MinStep:G4}..{total.MaxStep:G4}s, " +
                                $"mostly limited by {total.DominantLimit}");

            driver.ResetStepStats();
            driver.CollectStepStats = false;
            driver.Step(StepSeconds);
            bool stopped = driver.StepStats.Count == 0;
            if (!stopped)
                HarnessLog.Line($"[{Name}] FAIL: {driver.StepStats.Count} row(s) after ResetStepStats with collection off.");
            ok &= stopped;

            HarnessLog.Line($"[{Name}] {TestSupport.Verdict(ok)} (CSV {csv}).");
            return ok ? 0 : 1;
        }
        finally
        {
            PhysicsBubble._forceOffRails = forceOffRails;
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }

    private bool CheckRows(IReadOnlyList<StepStatsRow> rows, string vehicleId, int steps)
    {
        bool ok = true;
        bool[] seen = new bool[steps];
        foreach (StepStatsRow row in rows)
        {
            if (row.LeaderId != vehicleId)
                continue;
            if (row.Step < 0 || row.Step >= steps || seen[row.Step])
            {
                HarnessLog.Line($"[{Name}] FAIL: unexpected or repeated row for step {row.Step}.");
                ok = false;
                continue;
            }
            seen[row.Step] = true;

            long byLimit = 0;
            foreach (int count in row.SubstepsByLimit)
                byLimit += count;
            bool substeps = row.Substeps >= 1 && byLimit == row.Substeps;
            bool bounds = row.MinStep > 0.0 && row.MinStep <= row.MaxStep && row.MaxStep <= StepSeconds * (1.0 + 1e-9);
            if (!substeps || !bounds)
            {
                HarnessLog.Line($"[{Name}] FAIL step {row.Step}: substeps={row.Substeps} (by limit {byLimit}), " +
                                $"step {row.MinStep:G6}..{row.MaxStep:G6}s");
                ok = false;
            }
        }
        for (int i = 0; i < steps; i++)
        {
            if (!seen[i])
            {
                HarnessLog.Line($"[{Name}] FAIL: no row for step {i} of '{vehicleId}' ({rows.Count} row(s) in all).");
                ok = false;
            }
        }
        return ok;
    }
}
