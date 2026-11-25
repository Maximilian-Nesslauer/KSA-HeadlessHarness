using System.Collections.Generic;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Checks the TestSupport rocket and gimbal helpers on a real staged vehicle in vacuum:
//   1. Rated performance: MaxThrust and VacuumIsp at 0 Pa and 101325 Pa reproduce the
//      EngineController.VacuumData and SeaLevelData that PartTree.RecomputeRocketControls stores.
//   2. Live state while the first stage burns at full throttle: IsFed agrees with
//      EngineHasPropellant, Throttle reads 1, and MassFlow equals the rated vacuum mass flow.
//   3. The vehicle loses mass at the sum of the live MassFlow rates over a few seconds.
//   4. With a held pitch key the gimbals of the burning engines deflect toward their targets within
//      their range and slew-rate cap (ReadGimbalAngles).
// Skips when the Test Vehicle 1 save is missing.
public sealed class PropulsionRocketsTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;
    private const double RatedTol = 1e-5;     // relative; the same float computation in a different call
    private const double LiveFlowTol = 1e-3;  // relative; live nozzle state against the rated value
    private const double BurnedMassTol = 0.02;
    private const int WarmupSeconds = 2;
    private const int MeasureSeconds = 5;
    private const int MaxStagings = 8;

    public string Name => "rockets";

    public int Run(HeadlessSession session)
    {
        if (!PropulsionTestVehicles.SaveExists())
        {
            HarnessLog.Line($"[rockets] SKIP: no '{PropulsionTestVehicles.SaveId}' save in {VehicleSaves.SaveFolderPath}.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[rockets] SKIP: the loaded system has no home body.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool forceOffRails = PhysicsBubble._forceOffRails;
        bool ok = true;
        try
        {
            Vehicle vehicle = PropulsionTestVehicles.SpawnInOrbit(system, home, "HarnessRocketsTest", SpawnAltitudeM);
            SimDriver driver = session.CreateDriver();
            PhysicsBubble._forceOffRails = true;
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
            driver.Step(1.0);

            ok &= CheckRatedPerformance(vehicle);
            ok &= CheckBurn(vehicle, driver);
            ok &= CheckGimbalSteering(vehicle, driver);
        }
        finally
        {
            PhysicsBubble._forceOffRails = forceOffRails;
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        HarnessLog.Line($"[rockets] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckRatedPerformance(Vehicle vehicle)
    {
        bool ok = true;
        int engines = 0;
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            engines++;
            double vacuumThrust = engine.VacuumData.ThrustMax.Length();
            double seaLevelThrust = engine.SeaLevelData.ThrustMax.Length();
            double vacuumIsp = engine.VacuumData.MassFlowRateMax > 0f
                ? vacuumThrust / (engine.VacuumData.MassFlowRateMax * Constants.STANDARD_GRAVITY)
                : 0.0;
            bool engineOk = Close(TestSupport.MaxThrust(engine), vacuumThrust, RatedTol)
                            && Close(TestSupport.MaxThrust(engine, 101325.0), seaLevelThrust, RatedTol)
                            && Close(TestSupport.VacuumIsp(engine), vacuumIsp, RatedTol)
                            && Close(TestSupport.ComputePerformance(engine).MassFlowRateMax, engine.VacuumData.MassFlowRateMax, RatedTol)
                            && TestSupport.MaxThrust(engine, 0.0, 0.5f) <= vacuumThrust * (1.0 + RatedTol);
            HarnessLog.Line($"[rockets] rated '{engine.Parent.Id}': vac {TestSupport.MaxThrust(engine):F1}N (stored {vacuumThrust:F1}N) " +
                            $"Isp {TestSupport.VacuumIsp(engine):F2}s (stored {vacuumIsp:F2}s), SL {TestSupport.MaxThrust(engine, 101325.0):F1}N " +
                            $"(stored {seaLevelThrust:F1}N), solid={TestSupport.IsSolid(engine)} => {TestSupport.Verdict(engineOk)}");
            ok &= engineOk;
        }
        foreach (ThrusterController thruster in vehicle.Parts.Modules.Get<ThrusterController>())
        {
            double isp = TestSupport.VacuumIsp(thruster);
            bool thrusterOk = double.IsFinite(isp) && isp >= 0.0 && TestSupport.MaxThrust(thruster) >= 0.0;
            if (!thrusterOk)
                HarnessLog.Line($"[rockets] thruster '{thruster.Parent.Id}': Isp {isp} thrust {TestSupport.MaxThrust(thruster)} => FAIL");
            ok &= thrusterOk;
        }
        if (engines == 0)
        {
            HarnessLog.Line("[rockets] FAIL: the vehicle has no engine.");
            ok = false;
        }
        return ok;
    }

    private static bool CheckBurn(Vehicle vehicle, SimDriver driver)
    {
        TestSupport.SetManualControlInputs(vehicle, 1f, engineOn: true);
        SequenceList sequences = vehicle.Parts.SequenceList;
        int stagings = 0;
        while (!TestSupport.AnyActiveEngineFed(vehicle) && sequences.GetNextSequenceNumber() != -1 && stagings < MaxStagings)
        {
            sequences.ActivateNextSequence(vehicle);
            stagings++;
            driver.Step(1.0);
        }
        driver.Step(1.0, WarmupSeconds);

        bool ok = true;
        int firing = 0;
        double liveFlow = 0.0;
        bool anySolid = false;
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            bool fed = TestSupport.IsFed(vehicle, engine);
            bool consistent = fed == (engine.IsActive && TestSupport.EngineHasPropellant(vehicle, engine));
            if (!consistent)
            {
                HarnessLog.Line($"[rockets] '{engine.Parent.Id}': IsFed={fed} but active={engine.IsActive} propellant={TestSupport.EngineHasPropellant(vehicle, engine)} => FAIL");
                ok = false;
            }
            if (!fed)
                continue;

            firing++;
            double massFlow = TestSupport.MassFlow(vehicle, engine);
            float throttle = TestSupport.Throttle(vehicle, engine);
            liveFlow += massFlow;
            bool solid = TestSupport.IsSolid(engine);
            anySolid |= solid;
            foreach (Rocket rocket in engine.Rockets)
            {
                RocketState state = TestSupport.GetRocketState(vehicle, rocket);
                if (!state.IsPropellantAvailable)
                {
                    HarnessLog.Line($"[rockets] '{engine.Parent.Id}': a rocket of a fed engine reads no propellant => FAIL");
                    ok = false;
                }
            }
            // A solid motor follows its grain's thrust curve, not the rated value.
            bool engineOk = solid || (throttle == 1f && Close(massFlow, engine.VacuumData.MassFlowRateMax, LiveFlowTol));
            HarnessLog.Line($"[rockets] live '{engine.Parent.Id}': throttle {throttle:G4}, mass flow {massFlow:F4}kg/s " +
                            $"(rated {engine.VacuumData.MassFlowRateMax:F4}kg/s), solid={solid} => {TestSupport.Verdict(engineOk)}");
            ok &= engineOk;
        }
        if (firing == 0)
        {
            HarnessLog.Line($"[rockets] FAIL: no engine fed after {stagings} staging(s).");
            return false;
        }

        double m0 = vehicle.TotalMass;
        driver.Step(1.0, MeasureSeconds);
        double burned = m0 - vehicle.TotalMass;
        double expected = liveFlow * MeasureSeconds;
        if (!TestSupport.AnyActiveEngineFed(vehicle) || anySolid)
        {
            HarnessLog.Line($"[rockets] burned mass check skipped ({(anySolid ? "a solid motor burns on its own curve" : "the stage ran dry in the window")}).");
            return ok;
        }
        bool massOk = Close(burned, expected, BurnedMassTol);
        HarnessLog.Line($"[rockets] burned {burned:F3}kg in {MeasureSeconds}s, live mass flow predicts {expected:F3}kg " +
                        $"(tol {BurnedMassTol:P0}) => {TestSupport.Verdict(massOk)}");
        return ok && massOk;
    }

    // Steers with the burning engines: a held pitch-up key in Manual attitude mode makes the flight
    // computer deflect the gimbals (FlightComputer.ComputeTvcControl picks thrust vectoring while an
    // engine with a gimbal burns). Every reading must lie inside the gimbal's range, move no faster
    // than the slew-rate cap of GimbalAxis.UpdateState and target an angle inside the range, and at
    // least one gimbal must have followed its command off center.
    private static bool CheckGimbalSteering(Vehicle vehicle, SimDriver driver)
    {
        if (!TestSupport.AnyActiveEngineFed(vehicle))
        {
            HarnessLog.Line("[rockets] gimbal steering skipped: no engine is burning any more.");
            return true;
        }
        SetManualInputs(vehicle, new ManualControlInputs { EngineOn = true, EngineThrottle = 1f, ThrusterCommandFlags = ThrusterMapFlags.PitchUp });
        driver.Step(0.5);

        bool ok = true;
        int readings = 0;
        int following = 0;
        int expectedReadings = 0;
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            if (!TestSupport.IsFed(vehicle, engine))
                continue;
            HashSet<GimbalController> expectedControllers = new(ReferenceEqualityComparer.Instance);
            foreach (Rocket rocket in engine.Rockets)
                foreach (RocketNozzle nozzle in rocket.AllNozzles)
                    if (nozzle.Parent.GimbalAsmb is Gimbal gimbal)
                        expectedControllers.Add(gimbal.Controller);
            expectedReadings += expectedControllers.Count;
            foreach (GimbalReading gimbal in TestSupport.ReadGimbalAngles(vehicle, engine))
            {
                readings++;
                float rateCap = 1.0001f * gimbal.NaturalFrequency * MathF.Max(gimbal.MaxAngles.X, gimbal.MaxAngles.Y) / (5f * MathF.E);
                bool gimbalOk = gimbal.NaturalFrequency > 0f
                                && MathF.Abs(gimbal.Angles.X) <= gimbal.MaxAngles.X && MathF.Abs(gimbal.Angles.Y) <= gimbal.MaxAngles.Y
                                && MathF.Abs(gimbal.CommandAngles.X) <= gimbal.MaxAngles.X && MathF.Abs(gimbal.CommandAngles.Y) <= gimbal.MaxAngles.Y
                                && MathF.Abs(gimbal.AngleRates.X) <= rateCap && MathF.Abs(gimbal.AngleRates.Y) <= rateCap;
                if (Follows(gimbal.Angles.X, gimbal.CommandAngles.X, gimbal.MaxAngles.X) || Follows(gimbal.Angles.Y, gimbal.CommandAngles.Y, gimbal.MaxAngles.Y))
                    following++;
                HarnessLog.Line($"[rockets] gimbal of '{engine.Parent.Id}': angles ({gimbal.Angles.X:G4}, {gimbal.Angles.Y:G4})rad " +
                                $"target ({gimbal.CommandAngles.X:G4}, {gimbal.CommandAngles.Y:G4})rad rates ({gimbal.AngleRates.X:G4}, {gimbal.AngleRates.Y:G4})rad/s " +
                                $"max ({gimbal.MaxAngles.X:G4}, {gimbal.MaxAngles.Y:G4})rad w={gimbal.NaturalFrequency:G4} thrust {gimbal.TotalThrust:F0}N " +
                                $"=> {TestSupport.Verdict(gimbalOk)}");
                ok &= gimbalOk;
            }
        }
        SetManualInputs(vehicle, new ManualControlInputs { EngineOn = true, EngineThrottle = 1f });
        bool steered = readings == expectedReadings && (expectedReadings == 0 || following > 0);
        HarnessLog.Line($"[rockets] {readings} gimbal reading(s) on burning engines, {following} deflected toward their target => {TestSupport.Verdict(steered)}");
        return ok && steered;
    }

    // Off center and on the side of a nonzero target.
    private static bool Follows(float angle, float target, float maxAngle)
        => maxAngle > 0f && MathF.Abs(target) > 0.01f * maxAngle && MathF.Abs(angle) > 0.01f * maxAngle && MathF.Sign(angle) == MathF.Sign(target);

    // TestSupport.SetManualControlInputs sets throttle and engine only; a held rotation key needs the
    // thruster flags of the same private struct, Vehicle._manualControlInputs.
    private static void SetManualInputs(Vehicle vehicle, ManualControlInputs inputs)
    {
        TestSupport.GetManualControlInputs(vehicle) = inputs;
    }

    private static bool Close(double actual, double expected, double relTol)
        => Math.Abs(actual - expected) <= relTol * Math.Max(Math.Abs(expected), 1e-9);
}
