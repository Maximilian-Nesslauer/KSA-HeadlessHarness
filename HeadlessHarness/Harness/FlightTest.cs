using System.Collections.Generic;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Flies a player-built vehicle save the way a player flies it and asserts real physics headless:
//   1. A coasting orbit conserves orbital energy under the numerical integrator (forced off-rails).
//   2. Full throttle, then the staging key: burn the active stage until its propellant runs dry,
//      activate the next sequence (decouple / ignite via the game's own SequenceList), repeat until
//      the sequence list is spent. Each burn phase must consume propellant at the firing engines'
//      rated vacuum mass-flow rate.
//   3. The final state is fingerprinted and compared bit-for-bit against the previous run with the
//      same build and vehicle (reported in the log, not asserted).
// Numbers come from the real FlightComputer/PhysicsStates, so the assertions track genuine game
// behaviour with no separate re-implementation to drift.
//
// The vehicle comes from a save in the game's Vehicles folder, named via KSA_HEADLESS_VEHICLE.
// Unset, the test skips: which saves exist is machine-specific, so there is no meaningful default.
public sealed class FlightTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;  // above the home body's mean radius; near-vacuum, so VacuumData is the right expectation
    private const double EnergyDriftTol = 1e-3;       // relative; integrator drift over the coast
    private const double MassFlowTol = 0.02;          // relative; burned mass vs firing engines' rated flow
    private const int CoastSeconds = 60;
    private const int WarmupSeconds = 2;              // let engines reach steady thrust before measuring
    private const int MeasureSeconds = 10;            // mass-flow assertion window per burn phase
    private const int MaxFlightSeconds = 3600;        // runaway guard for the whole staged flight

    public string Name => "flight";

    public int Run(HeadlessSession session)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"[flight] SKIP: {TestSupport.VehicleEnvVar} not set (name a save in the game's Vehicles folder to run this test).");
            return 0;
        }

        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home || home is not Astronomical homeBody)
        {
            HarnessLog.Line("[flight] FAIL: the loaded system has no home body to orbit.");
            return 1;
        }

        SimTime now = Universe.GetElapsedSimTime();
        Orbit orbit = VehicleSpawner.CircularCci(home, homeBody.MeanRadius + SpawnAltitudeM, now);

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        Vehicle vehicle;
        try
        {
            vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "HarnessFlightTest", orbit);
        }
        catch (InvalidOperationException e)
        {
            HarnessLog.Line($"[flight] FAIL: {e.Message}");
            return 1;
        }
        LogVehicle(vehicle, saveId);

        SimDriver driver = session.CreateDriver();
        bool ok = true;
        try
        {
            VehicleUpdateTask._forceOffRails = true;
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;

            ok &= RunCoastTest(vehicle, driver);
            ok &= RunStagedFlight(vehicle, driver);
        }
        finally
        {
            VehicleUpdateTask._forceOffRails = false;
            // Tear down the flown vehicle and every stage it shed (staging splits register new
            // vehicles under the same parent), so later tests do not keep ticking them.
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        LogDeterminismSignature(vehicle, saveId);

        HarnessLog.Line($"[flight] {TestSupport.Verdict(ok)} (staged flight).");
        return ok ? 0 : 1;
    }

    private static void LogVehicle(Vehicle vehicle, string saveId)
    {
        Orbit orbit = vehicle.Orbit;
        HarnessLog.Line($"[flight] '{saveId}' spawned: parent='{(orbit.Parent as Astronomical)?.Id}', " +
                        $"SMA={orbit.SemiMajorAxis:E4}m ecc={orbit.Eccentricity:F4}");
        HarnessLog.Line($"[flight]   mass={vehicle.TotalMass:F1}kg inert={vehicle.InertMass:F1}kg prop={vehicle.PropellantMass:F1}kg, " +
                        $"{vehicle.Parts.Modules.Get<EngineController>().Length} engine(s), " +
                        $"next sequence {vehicle.Parts.SequenceList.GetNextSequenceNumber()}");
    }

    private static bool RunCoastTest(Vehicle vehicle, SimDriver driver)
    {
        double mu = vehicle.Orbit.Parent.Mu;
        TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
        StateVectors s0 = vehicle.Orbit.StateVectors;
        double e0 = Orbit.GetOrbitalEnergy(in s0, mu);
        driver.Step(1.0, CoastSeconds);
        StateVectors s1 = vehicle.Orbit.StateVectors;
        double e1 = Orbit.GetOrbitalEnergy(in s1, mu);
        double drift = e0 != 0.0 ? Math.Abs((e1 - e0) / e0) : double.NaN;
        bool ok = drift < EnergyDriftTol;
        HarnessLog.Line($"[flight] TEST coast energy: drift={drift:E3} (tol {EnergyDriftTol:E0}) => {TestSupport.Verdict(ok)}");
        return ok;
    }

    // The player loop: full throttle, stage when the active engines run dry, until the sequence list
    // is spent. Sequence activation goes through the game's own input queue, so each
    // ActivateNextSequence takes effect on the following Step (same one-frame latency as in the
    // running game).
    private static bool RunStagedFlight(Vehicle vehicle, SimDriver driver)
    {
        TestSupport.SetManualControlInputs(vehicle, 1f, engineOn: true);
        SequenceList sequences = vehicle.Parts.SequenceList;

        // No special liftoff step: when nothing is firing, the loop presses the staging key, which
        // covers ignition at t=0 (a save with inactive engines), staging between burns, and a save
        // made mid-flight (whose current stage keeps burning before anything new is activated).
        int elapsed = 0;
        int stagings = 0;
        bool ok = true;
        int attempted = 0;
        int measured = 0;
        bool measuredThisPhase = false;
        while (elapsed < MaxFlightSeconds)
        {
            if (TestSupport.AnyActiveEngineFed(vehicle))
            {
                if (!measuredThisPhase)
                {
                    ok &= MeasureBurnPhase(vehicle, driver, ref elapsed, ref attempted, ref measured);
                    measuredThisPhase = true;
                }
                else
                {
                    driver.Step(1.0);
                    elapsed++;
                }
            }
            else
            {
                if (sequences.GetNextSequenceNumber() == -1)
                    break;
                sequences.ActivateNextSequence(vehicle);
                stagings++;
                measuredThisPhase = false;
                driver.Step(1.0);
                elapsed++;
            }
        }

        if (elapsed >= MaxFlightSeconds)
        {
            HarnessLog.Line($"[flight] FAIL: flight guard hit after {MaxFlightSeconds}s of sim time with propellant still burning.");
            ok = false;
        }
        if (measured == 0)
        {
            HarnessLog.Line(attempted > 0
                ? "[flight] FAIL: burn phases ran but every stage went dry before completing a measurement window."
                : "[flight] FAIL: no burn phase ran (no active engine ever had propellant).");
            ok = false;
        }
        bool sequencesSpent = vehicle.Parts.SequenceList.GetNextSequenceNumber() == -1;
        if (!sequencesSpent)
        {
            HarnessLog.Line($"[flight] FAIL: flight ended with unactivated sequences remaining (next {vehicle.Parts.SequenceList.GetNextSequenceNumber()}).");
            ok = false;
        }
        HarnessLog.Line($"[flight] flight summary: {stagings} sequence activation(s), {measured} burn phase(s) measured, " +
                        $"{elapsed}s sim time, final mass={vehicle.TotalMass:F1}kg prop={vehicle.PropellantMass:F1}kg");
        return ok;
    }

    // Asserts one burn phase: after a short warmup, the vehicle must lose mass at the rated vacuum
    // mass-flow rate of the engines that are active and fed at steady state. The warmup exists
    // because per-core propellant availability bootstraps a step late, which would skew the window.
    private static bool MeasureBurnPhase(Vehicle vehicle, SimDriver driver, ref int elapsed, ref int attempted, ref int measured)
    {
        driver.Step(1.0, WarmupSeconds);
        elapsed += WarmupSeconds;

        double expectedMdot = 0.0;
        double firingThrust = 0.0;
        int firing = 0;
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            if (engine.IsActive && TestSupport.EngineHasPropellant(vehicle, engine))
            {
                expectedMdot += engine.VacuumData.MassFlowRateMax;
                firingThrust += engine.VacuumData.ThrustMax.Length();
                firing++;
            }
        }
        if (firing == 0)
            return true; // ran dry during the warmup; the caller stages next iteration
        attempted++;

        double m0 = vehicle.TotalMass;
        driver.Step(1.0, MeasureSeconds);
        elapsed += MeasureSeconds;
        double m1 = vehicle.TotalMass;
        double dm = m0 - m1;

        if (!TestSupport.AnyActiveEngineFed(vehicle))
        {
            // The stage ran dry inside the window, so dm covers only part of it; the mass-flow
            // assertion would be meaningless. The next phase (after staging) still gets measured.
            HarnessLog.Line($"[flight] burn phase: {firing} engine(s) ran dry inside the {MeasureSeconds}s window; mass-flow assertion skipped.");
            return true;
        }

        measured++;
        double expectedDm = expectedMdot * MeasureSeconds;
        double err = expectedDm > 0 ? Math.Abs(dm - expectedDm) / expectedDm : double.NaN;
        double firingVe = expectedMdot > 0 ? firingThrust / expectedMdot : 0.0;
        double idealDv = (m1 > 0 && dm > 0) ? firingVe * Math.Log(m0 / m1) : 0.0;
        bool ok = dm > 0 && err < MassFlowTol;
        HarnessLog.Line($"[flight] TEST burn mass-flow: {firing} engine(s) firing, dm={dm:F3}kg expect={expectedDm:F3}kg " +
                        $"err={err:E3} (tol {MassFlowTol:P0}) windowDv={idealDv:F2}m/s => {TestSupport.Verdict(ok)}");
        return ok;
    }

    // Compare an exact-bits signature of the final state against the previous run's (persisted).
    // First run establishes the baseline; a later run with the same build and vehicle must reproduce
    // it bit-for-bit. Keyed per game build (physics legitimately change across builds) and per
    // vehicle save (different vehicles legitimately end in different states).
    private static void LogDeterminismSignature(Vehicle vehicle, string saveId)
    {
        string vehicleKey = string.Join("_", saveId.Split(Path.GetInvalidFileNameChars()));
        // Lives in the shared data directory (not the per-run log dir naming) because the baseline
        // must persist across runs; cross-run access is serialized by the run mutex in HarnessMain.
        string sigFile = Path.Combine(HarnessLog.DataDirectory,
            $"{VersionInfo.Current.VersionString}.{vehicleKey}.sig");

        StateVectors f = vehicle.Orbit.StateVectors;
        string sig = string.Join(",", new[]
        {
            Bits(f.PositionCci.X), Bits(f.PositionCci.Y), Bits(f.PositionCci.Z),
            Bits(f.VelocityCci.X), Bits(f.VelocityCci.Y), Bits(f.VelocityCci.Z),
            Bits(vehicle.TotalMass),
        });

        string? prev = null;
        try
        {
            if (File.Exists(sigFile))
                prev = File.ReadAllText(sigFile).Trim();
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[flight] determinism: could not read previous signature ({e.Message}); leaving baseline.");
            return; // keep the stored baseline rather than clobber it on a transient read error.
        }

        if (prev == null)
            HarnessLog.Line("[flight] determinism: baseline established (run again with the same build to compare).");
        else if (prev == sig)
            HarnessLog.Line("[flight] determinism: MATCH previous run (bit-for-bit).");
        else
            HarnessLog.Line($"[flight] determinism: DIFFER from previous run\n  prev={prev}\n  now ={sig}");

        try { File.WriteAllText(sigFile, sig); }
        catch (Exception e)
        {
            HarnessLog.Line($"[flight] determinism: could not write signature ({e.Message}); next run will not compare.");
        }
    }

    private static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);
}
