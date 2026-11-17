using System.Collections.Generic;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Step statistics: after each Step, the substep counts of every physics bubble, read from
// PhysicsBubble.FrameStepDetail. PhysicsBubble.Prepare resets that detail at the start of every
// vehicle solver pass, so after Step it holds exactly the step just taken. Only the deterministic
// fields are kept (segments, substeps and the limit that bounded each, flight computer runs, the
// smallest and largest substep, the wake requests of the flight computer), never the tick timings.
public sealed partial class SimDriver
{
    private readonly List<StepStatsRow> _stepStats = new List<StepStatsRow>();
    private int _statsStep;

    // Off by default. On, every Step appends one row per physics bubble to StepStats.
    public bool CollectStepStats { get; set; }

    public IReadOnlyList<StepStatsRow> StepStats => _stepStats;

    // Drops the collected rows and starts the step count again at 0.
    public void ResetStepStats()
    {
        _stepStats.Clear();
        _statsStep = 0;
    }

    // The collected rows summed per bubble leader (the first vehicle of the bubble), ordered by
    // leader id.
    public IReadOnlyList<StepStatsTotal> SummarizeStepStats()
    {
        SortedDictionary<string, StepStatsTotal> totals = new SortedDictionary<string, StepStatsTotal>(StringComparer.Ordinal);
        foreach (StepStatsRow row in _stepStats)
        {
            totals.TryGetValue(row.LeaderId, out StepStatsTotal? total);
            totals[row.LeaderId] = total == null ? StepStatsTotal.From(row) : total.Add(row);
        }
        return new List<StepStatsTotal>(totals.Values);
    }

    // Writes the collected rows as a CSV next to the run log (HarnessData) and returns its path.
    public string WriteStepStats(string tag = "step-stats")
    {
        string[] limits = StepStatsRow.LimitNames;
        List<string> header = new List<string>
        {
            "step", "time_s", "dt_s", "leader", "parent", "vehicles", "segments", "substeps",
            "fc_runs", "min_step_s", "max_step_s", "min_wake_s", "max_wake_s",
        };
        foreach (string limit in limits)
            header.Add("limit_" + limit);
        HarnessData data = HarnessData.Create(tag, string.Join(",", header));

        foreach (StepStatsRow row in _stepStats)
        {
            List<object?> cells = new List<object?>
            {
                row.Step, row.TimeSeconds, row.DeltaTime, row.LeaderId, row.ParentId, row.Vehicles, row.Segments,
                row.Substeps, row.FlightComputerRuns, Finite(row.MinStep), Finite(row.MaxStep),
                Finite(row.MinRequestedWake), Finite(row.MaxRequestedWake),
            };
            foreach (int count in row.SubstepsByLimit)
                cells.Add(count);
            data.AppendRow(cells.ToArray());
        }
        return data.FilePath;
    }

    partial void AfterStep(double dt)
    {
        if (!CollectStepStats)
            return;
        int step = _statsStep++;
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return;

        // Several vehicles share a bubble; each bubble is recorded once, under its first member.
        HashSet<PhysicsBubble> seen = new HashSet<PhysicsBubble>(ReferenceEqualityComparer.Instance);
        List<Vehicle> members = new List<Vehicle>();
        double time = Elapsed.Seconds();
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Vehicle { IsDisposed: false } vehicle)
                continue;
            PhysicsBubble? bubble = TestSupport.GetPhysicsBubble(vehicle);
            if (bubble == null || !seen.Add(bubble))
                continue;
            members.Clear();
            bubble.CollectVehicles(members);
            string leader = members.Count > 0 ? members[0].Id : vehicle.Id;
            _stepStats.Add(StepStatsRow.From(step, time, dt, leader, (bubble.Parent as Astronomical)?.Id ?? string.Empty,
                bubble.NumVehicles, in bubble.FrameStepDetail));
        }
    }

    private static object Finite(double value) => double.IsFinite(value) ? value : string.Empty;
}

// One physics bubble in one SimDriver.Step. MinStep and MinRequestedWake are positive infinity, and
// MaxRequestedWake is 0, when the bubble took no substep or the flight computer asked for no wake.
public sealed record StepStatsRow(
    int Step,
    double TimeSeconds,
    double DeltaTime,
    string LeaderId,
    string ParentId,
    int Vehicles,
    long Segments,
    long Substeps,
    long FlightComputerRuns,
    double MinStep,
    double MaxStep,
    double MinRequestedWake,
    double MaxRequestedWake,
    IReadOnlyList<int> SubstepsByLimit)
{
    // The PhysicsStepLimit names in enum order, without the EnumCount sentinel. SubstepsByLimit is
    // indexed the same way.
    public static readonly string[] LimitNames = BuildLimitNames();

    public int SubstepsLimitedBy(PhysicsStepLimit limit) => SubstepsByLimit[(int)limit];

    internal static StepStatsRow From(int step, double time, double dt, string leader, string parent, int vehicles,
        in PhysicsBubbleStepDetail detail)
    {
        int[] byLimit = new int[(int)PhysicsStepLimit.EnumCount];
        for (int i = 0; i < byLimit.Length; i++)
            byLimit[i] = detail.SubstepsByLimit[(PhysicsStepLimit)i];
        return new StepStatsRow(step, time, dt, leader, parent, vehicles, detail.Segments, detail.Substeps,
            detail.FlightComputerRuns, detail.MinStep, detail.MaxStep, detail.MinRequestedWake, detail.MaxRequestedWake, byLimit);
    }

    private static string[] BuildLimitNames()
    {
        string[] names = new string[(int)PhysicsStepLimit.EnumCount];
        for (int i = 0; i < names.Length; i++)
            names[i] = ((PhysicsStepLimit)i).ToString();
        return names;
    }
}

// The rows of one bubble leader summed over the collected steps.
public sealed record StepStatsTotal(
    string LeaderId,
    int Steps,
    long Segments,
    long Substeps,
    long FlightComputerRuns,
    double MinStep,
    double MaxStep,
    IReadOnlyList<long> SubstepsByLimit)
{
    internal static StepStatsTotal From(StepStatsRow row)
    {
        long[] byLimit = new long[row.SubstepsByLimit.Count];
        for (int i = 0; i < byLimit.Length; i++)
            byLimit[i] = row.SubstepsByLimit[i];
        return new StepStatsTotal(row.LeaderId, 1, row.Segments, row.Substeps, row.FlightComputerRuns, row.MinStep, row.MaxStep, byLimit);
    }

    internal StepStatsTotal Add(StepStatsRow row)
    {
        long[] byLimit = new long[SubstepsByLimit.Count];
        for (int i = 0; i < byLimit.Length; i++)
            byLimit[i] = SubstepsByLimit[i] + row.SubstepsByLimit[i];
        return new StepStatsTotal(LeaderId, Steps + 1, Segments + row.Segments, Substeps + row.Substeps,
            FlightComputerRuns + row.FlightComputerRuns, Math.Min(MinStep, row.MinStep), Math.Max(MaxStep, row.MaxStep), byLimit);
    }

    // The limit that bounded the most substeps, or None when there were none.
    public PhysicsStepLimit DominantLimit
    {
        get
        {
            PhysicsStepLimit best = PhysicsStepLimit.None;
            long most = 0;
            for (int i = 1; i < SubstepsByLimit.Count; i++)
            {
                if (SubstepsByLimit[i] > most)
                {
                    most = SubstepsByLimit[i];
                    best = (PhysicsStepLimit)i;
                }
            }
            return best;
        }
    }
}
