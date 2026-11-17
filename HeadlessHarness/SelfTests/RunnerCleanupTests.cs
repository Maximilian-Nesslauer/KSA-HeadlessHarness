using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;
using KSA.Kfs;

namespace HeadlessHarness.SelfTests;

// The cleanup the runner does after each test, proven by a pair that runs in name order:
// runner-cleanup changes every global the runner restores and leaves a spawned vehicle behind, and
// runner-cleanup-verify then checks that each one is back. Opt-in, because the first one makes the
// run log report a LEAK on purpose. Run both: -Tests runner-cleanup,runner-cleanup-verify.
public sealed class RunnerCleanupTest : IHarnessTest
{
    internal const string LeakedVehicleId = "HarnessLeakSat";

    // Read by runner-cleanup-verify; the values runner-cleanup found before it changed them.
    internal static double? SpeedBefore;
    internal static bool RadiationBefore;
    internal static bool LifeSupportBefore;
    internal static bool KfsBefore;

    public string Name => "runner-cleanup";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        SpeedBefore = Universe.SimulationSpeed;
        RadiationBefore = Universe.Rules.Radiation;
        LifeSupportBefore = Universe.Rules.LifeSupport;
        KfsBefore = KfsPrototype.Enabled;

        // With alert true the game also queues a TimedAlert, which the cleanup clears.
        Universe.SetSimulationSpeed(SpeedBefore.Value * 4.0 + 1.0, alert: true);
        Universe.Rules.Radiation = !RadiationBefore;
        Universe.Rules.LifeSupport = !LifeSupportBefore;
        PhysicsBubble._forceOffRails = true;
        KfsPrototype.Enabled = !KfsBefore;
        t.Info($"changed speed {SpeedBefore:G6} -> {Universe.SimulationSpeed:G6}, radiation -> {Universe.Rules.Radiation}, " +
               $"life support -> {Universe.Rules.LifeSupport}, force off rails on, kFS -> {KfsPrototype.Enabled}");

        Vehicle? source = null;
        CelestialSystem system = session.System;
        for (int i = 0; i < system.Count && source == null; i++)
            source = system.GetIndex(i) as Vehicle;
        if (source == null)
        {
            t.Skip("the loaded system has no vehicle to copy, so no vehicle is left behind");
        }
        else
        {
            Orbit orbit = VehicleSpawner.CircularCci(source.Orbit.Parent, source.Orbit.SemiMajorAxis + 700_000.0, Universe.GetElapsedTime());
            Vehicle leaked = VehicleSpawner.SpawnCopy(source, source.Orbit.Parent, LeakedVehicleId, orbit);
            t.Check("spawned the vehicle to leave behind", leaked.Id.StartsWith(LeakedVehicleId, StringComparison.Ordinal), leaked.Id);
        }
        InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(17) });
        return t.Finish();
    }
}

public sealed class RunnerCleanupVerifyTest : IHarnessTest
{
    public string Name => "runner-cleanup-verify";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        if (RunnerCleanupTest.SpeedBefore is not double speed)
        {
            t.Skip("runner-cleanup did not run before this test");
            return t.Finish();
        }
        t.CheckAbs("simulation speed put back", Universe.SimulationSpeed, speed, 0.0);
        t.Check("radiation rule put back", Universe.Rules.Radiation == RunnerCleanupTest.RadiationBefore);
        t.Check("life support rule put back", Universe.Rules.LifeSupport == RunnerCleanupTest.LifeSupportBefore);
        t.Check("force off rails off", !PhysicsBubble._forceOffRails);
        t.Check("kFS switch put back", KfsPrototype.Enabled == RunnerCleanupTest.KfsBefore);
        t.Check("pending speed request discarded", InputEvents.SimSpeedBuffer.Count == 0);
        session.CreateDriver().Step(0.1);
        t.CheckAbs("next step does not apply the previous test's request", Universe.SimulationSpeed, speed, 0.0);
        bool found = false;
        CelestialSystem system = session.System;
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle v && v.Id.StartsWith(RunnerCleanupTest.LeakedVehicleId, StringComparison.Ordinal))
                found = true;
        }
        t.Check("the leaked vehicle is gone", !found);
        return t.Finish();
    }
}

// Hangs on purpose so the watchdog has to end the run: the run then exits with code 3 and the
// results file lists this test as a timeout. Opt-in: -Tests runner-watchdog.
public sealed class RunnerWatchdogTest : IHarnessTest
{
    public string Name => "runner-watchdog";

    public bool OptIn => true;

    public int? TimeoutSeconds => 2;

    public int Run(HeadlessSession session)
    {
        HarnessLog.Line("[runner-watchdog] sleeping past the 2 s budget; the watchdog must end the run with exit code 3.");
        Thread.Sleep(TimeSpan.FromSeconds(60));
        HarnessLog.Line("[runner-watchdog] FAIL: the watchdog did not end the run.");
        return 1;
    }
}
