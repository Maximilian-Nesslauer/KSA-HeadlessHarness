using System.Collections.Generic;
using Brutal.Numerics;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// The harness entry, run by Mod.OnBeforeMain: brings the game up GPU-free, validates the session
// with content-agnostic smoke checks, then runs every discovered IHarnessTest (the harness's own
// plus consumer tests). Returns a process exit code; the process exits before KSA Program.Main.
//
// Exit codes: 0 = all tests passed; 1 = at least one test failed; 2 = infrastructure failure
// (bring-up broke, the session did not validate, or a consumer assembly failed to load), meaning
// the test results are not trustworthy.
internal static class HarnessMain
{
    public const int ExitPass = 0;
    public const int ExitTestFailure = 1;
    public const int ExitInfrastructureFailure = 2;

    public static int Run()
    {
        HarnessLog.Reset();
        HarnessLog.Line("[harness] start");

        HeadlessSession session = new HeadlessSession();
        try
        {
            session.BringUp();
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] INFRA FAIL: exception during bring-up:\n{e}");
            return ExitInfrastructureFailure;
        }

        try
        {
            if (!ValidateSession(session))
                return ExitInfrastructureFailure;

            HarnessRunner.RunResult result = HarnessRunner.RunDiscovered(session);
            if (result.InfrastructureFailures > 0)
            {
                HarnessLog.Line($"[harness] overall: INFRA FAIL ({result.InfrastructureFailures} infrastructure failure(s), " +
                                $"{result.TestFailures} test failure(s)).");
                return ExitInfrastructureFailure;
            }
            HarnessLog.Line($"[harness] overall: {(result.TestFailures == 0 ? "ALL PASS" : $"{result.TestFailures} test failure(s)")}.");
            return result.TestFailures == 0 ? ExitPass : ExitTestFailure;
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] INFRA FAIL: unexpected exception outside any test:\n{e}");
            return ExitInfrastructureFailure;
        }
    }

    // Content-agnostic checks that the GPU-free session is genuinely alive: bodies loaded, a world
    // sun resolved, and the solvers tick (an orbiting body moves). No stock entity names, so this
    // holds for any star system a mod setup loads.
    private static bool ValidateSession(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        if (system.Count <= 0)
        {
            HarnessLog.Line("[smoke] INFRA FAIL: system has no bodies.");
            return false;
        }
        if (Universe.WorldSun == null)
        {
            HarnessLog.Line("[smoke] INFRA FAIL: no world sun resolved.");
            return false;
        }

        LogInventory(system, out Celestial? mover);

        SimDriver driver = session.CreateDriver();
        driver.StepOrbits = true;
        if (mover != null)
        {
            double3 before = mover.Orbit.StateVectors.PositionCci;
            driver.Step(3600.0, 24); // one day, hourly steps
            double moved = (mover.Orbit.StateVectors.PositionCci - before).Length();
            if (moved <= 0.0)
            {
                HarnessLog.Line($"[smoke] INFRA FAIL: '{mover.Id}' did not move over one simulated day.");
                return false;
            }
            HarnessLog.Line($"[smoke] solver tick GPU-free: '{mover.Id}' moved {moved:E3}m over 1 day.");
        }
        else
        {
            HarnessLog.Line("[smoke] no orbiting celestial besides the world sun; skipping the solver movement check.");
        }

        HarnessLog.Line("[smoke] PASS (GPU-free system load + solver tick).");
        return true;
    }

    private static void LogInventory(CelestialSystem system, out Celestial? mover)
    {
        SortedDictionary<string, int> histogram = new();
        int vehicles = 0;
        mover = null;
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Astronomical a)
                continue;
            string type = a.GetType().Name;
            histogram[type] = histogram.GetValueOrDefault(type) + 1;
            if (a is Vehicle)
                vehicles++;
            if (a is Celestial c && !ReferenceEquals(c, Universe.WorldSun) && mover == null)
                mover = c;
        }

        HarnessLog.Line($"[smoke] system '{system.Id}' loaded GPU-free: {system.Count} bodies " +
                        $"({vehicles} vehicles), world sun '{Universe.WorldSun?.Id}'.");
        foreach (KeyValuePair<string, int> kv in histogram)
            HarnessLog.Line($"[smoke]   {kv.Value,3} x {kv.Key}");
    }
}
