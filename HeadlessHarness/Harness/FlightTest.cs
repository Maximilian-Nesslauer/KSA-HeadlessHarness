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
//   3. The final state is fingerprinted (Fingerprint.Of: the vehicle's save record and its state
//      bits) and compared bit-for-bit against the previous run with the same build, system, vehicle,
//      profile folder, rules and spawn epoch. A difference is reported in the log, and fails the test
//      only with KSA_HEADLESS_STRICT_DETERMINISM=1.
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

    // "1" turns a determinism DIFFER at the same spawn epoch into a test failure.
    public const string StrictDeterminismEnvVar = "KSA_HEADLESS_STRICT_DETERMINISM";

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
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[flight] FAIL: the loaded system has no home body to orbit.");
            return 1;
        }

        UniverseTime now = Universe.GetElapsedTime();
        Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, now);

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        // Read while the vehicle is live. The cleanup below disposes it, and Vehicle.Dispose only
        // happens to leave the orbit chain and mass props readable.
        StateVectors finalState = default;
        double finalMass = 0.0;
        Fingerprint? finalPrint = null;
        string finalXml = string.Empty;
        // The spawn runs inside the cleanup scope on purpose: Vehicle.CreateVehicle registers with
        // the CelestialSystem inside Astronomical's constructor, so a spawn helper that throws after
        // that point has already put a live vehicle in the system, and it would keep ticking through
        // every later test.
        try
        {
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

            PhysicsBubble._forceOffRails = true;
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;

            ok &= RunCoastTest(vehicle, driver);
            ok &= RunStagedFlight(vehicle, driver);

            finalState = vehicle.Orbit.StateVectors;
            finalMass = vehicle.TotalMass;
            finalPrint = Fingerprint.Of(vehicle, out finalXml);
        }
        finally
        {
            PhysicsBubble._forceOffRails = false;
            // Tear down the flown vehicle and every stage it shed (staging splits register new
            // vehicles under the same parent), so later tests do not keep ticking them.
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        if (finalPrint != null)
            ok &= CheckDeterminismSignature(system.Id, saveId, now, in finalState, finalMass, finalPrint, finalXml);

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
        int solidPhases = 0;
        bool measuredThisPhase = false;
        while (elapsed < MaxFlightSeconds)
        {
            if (TestSupport.AnyActiveEngineFed(vehicle))
            {
                if (!measuredThisPhase)
                {
                    ok &= MeasureBurnPhase(vehicle, driver, ref elapsed, ref attempted, ref measured, ref solidPhases);
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
        if (measured == 0 && solidPhases > 0 && solidPhases == attempted)
        {
            HarnessLog.Line($"[flight] every burn phase had a solid rocket firing ({solidPhases}), so no mass flow was measured.");
        }
        else if (measured == 0)
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
    private static bool MeasureBurnPhase(Vehicle vehicle, SimDriver driver, ref int elapsed, ref int attempted, ref int measured,
        ref int solidPhases)
    {
        driver.Step(1.0, WarmupSeconds);
        elapsed += WarmupSeconds;

        double expectedMdot = 0.0;
        double firingThrust = 0.0;
        int firing = 0;
        int solids = 0;
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            if (!engine.IsActive || !TestSupport.EngineHasPropellant(vehicle, engine))
                continue;
            // VacuumData rates a solid motor at the default chamber pressure, while its real burn
            // follows the grain (SolidRocket.UpdateState through SolidMotor.UpdateBurn), so a solid
            // has no rated mass flow to hold the burn to.
            if (HasSolidRocket(engine))
            {
                solids++;
                continue;
            }
            expectedMdot += engine.VacuumData.MassFlowRateMax;
            firingThrust += engine.VacuumData.ThrustMax.Length();
            firing++;
        }
        if (solids > 0)
        {
            // The vehicle mass falls with the solids' burn too, and the window cannot tell the two
            // apart, so the phase is not measured.
            attempted++;
            solidPhases++;
            HarnessLog.Line($"[flight] burn phase: {solids} solid rocket engine(s) firing beside {firing} other engine(s); " +
                            "the burn follows the grain, not the rated mass flow, so the mass-flow assertion is skipped.");
            return true;
        }
        if (firing == 0)
        {
            // Counted as attempted so the end-of-flight diagnostic says the stage went dry rather
            // than claiming no engine ever had propellant, which the caller's check disproves.
            attempted++;
            HarnessLog.Line($"[flight] burn phase: every active engine ran dry inside the {WarmupSeconds}s warmup; nothing to measure.");
            return true;
        }
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

    private static bool HasSolidRocket(EngineController engine)
    {
        foreach (Rocket rocket in engine.Rockets)
        {
            if (rocket is SolidRocket)
                return true;
        }
        return false;
    }

    // Exact-bits comparison against the previous run, keyed per game build, star system, vehicle
    // save, profile folder and game rules, because each legitimately changes the outcome (the profile
    // folder holds the save and the mods, so two profiles never compare). So does the spawn epoch,
    // which every SimDriver.Step in the whole run moves: it leads the signature and is compared
    // separately, so filtering the suite or adding a consumer test reads as NOT COMPARABLE rather
    // than a DIFFER that means nothing. Returns false only for a DIFFER in strict mode. The save record
    // is kept next to the signature, so a DIFFER in the record names its first difference.
    private static bool CheckDeterminismSignature(string systemId, string saveId, UniverseTime spawnTime,
        in StateVectors finalState, double finalMass, Fingerprint print, string saveXml)
    {
        bool strict = Environment.GetEnvironmentVariable(StrictDeterminismEnvVar) == "1";
        string vehicleKey = string.Join("_", saveId.Split(Path.GetInvalidFileNameChars()));
        string systemKey = string.Join("_", systemId.Split(Path.GetInvalidFileNameChars()));
        // Constants.DocumentsFolderPath is the profile folder the game runs in (StarMap points it at
        // an instance folder). Hashed, because a path does not fit in a file name.
        string profileKey = Fingerprint.Sha256(Path.GetFullPath(Constants.DocumentsFolderPath).ToUpperInvariant())[..8];
        GameRules rules = Universe.Rules;
        string rulesKey = $"rad{(rules.Radiation ? 1 : 0)}ls{(rules.LifeSupport ? 1 : 0)}";
        // Lives in the shared data directory (not the per-run log dir naming) because the baseline
        // must persist across runs; cross-run access is serialized by the run mutex in HarnessMain.
        string sigFile = Path.Combine(HarnessLog.DataDirectory,
            $"{VersionInfo.Current.VersionString}.{systemKey}.{vehicleKey}.{profileKey}.{rulesKey}.sig");

        string epoch = Bits(spawnTime.Seconds()).ToString();
        string state = string.Join(",", new[]
        {
            Bits(finalState.PositionCci.X), Bits(finalState.PositionCci.Y), Bits(finalState.PositionCci.Z),
            Bits(finalState.VelocityCci.X), Bits(finalState.VelocityCci.Y), Bits(finalState.VelocityCci.Z),
            Bits(finalMass),
        });
        string sig = $"{epoch};{state};{print.Signature}";
        bool ok = true;

        string? prev = null;
        try
        {
            if (File.Exists(sigFile))
                prev = File.ReadAllText(sigFile).Trim();
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[flight] determinism: could not read previous signature ({e.Message}); leaving baseline.");
            return true; // keep the stored baseline rather than clobber it on a transient read error.
        }

        if (prev == null)
            HarnessLog.Line("[flight] determinism: baseline established (run again with the same build and test selection to compare).");
        else if (prev == sig)
            HarnessLog.Line("[flight] determinism: MATCH previous run (bit-for-bit).");
        else if (prev.IndexOf(';') < 0)
            HarnessLog.Line("[flight] determinism: baseline replaced (the stored one predates the spawn-epoch field and cannot be compared).");
        else if (prev[..prev.IndexOf(';')] != epoch)
            HarnessLog.Line("[flight] determinism: NOT COMPARABLE - the previous run spawned at a different sim time, " +
                            "so a different set of tests ran before this one. Re-run with the same test selection to compare.");
        else
        {
            ok = !strict;
            HarnessLog.Line("[flight] determinism: DIFFER from previous run at the same spawn epoch" +
                            (strict ? $" => FAIL ({StrictDeterminismEnvVar}=1)" : string.Empty) +
                            $"\n  prev={prev}\n  now ={sig}");
            LogRecordDifference(sigFile + ".xml", saveXml);
        }

        try
        {
            File.WriteAllText(sigFile, sig);
            File.WriteAllText(sigFile + ".xml", saveXml);
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[flight] determinism: could not write signature ({e.Message}); next run will not compare.");
        }
        return ok;
    }

    private static void LogRecordDifference(string previousXmlFile, string saveXml)
    {
        try
        {
            if (!File.Exists(previousXmlFile))
                return;
            string? difference = SaveRoundTrip.FirstDifference(File.ReadAllText(previousXmlFile), saveXml);
            HarnessLog.Line($"[flight] determinism: save record {(difference == null ? "equal" : $"first difference {difference}")}");
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[flight] determinism: could not compare the save records ({e.Message}).");
        }
    }

    private static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);
}
