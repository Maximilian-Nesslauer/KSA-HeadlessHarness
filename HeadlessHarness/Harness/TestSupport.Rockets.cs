using System.Collections.Generic;
using KSA;

namespace HeadlessHarness.Harness;

// Rocket readback for tests: the live per-rocket and per-core states of a vehicle, and the rated
// performance the game computes from the rocket templates. The live states live in the part tree's
// state lists (ModuleStateful<,,,>.TryGetFrom on PartTree.States), which the solver writes during a
// step, so read them between SimDriver steps only.
public static partial class TestSupport
{
    // The per-rocket state the solver keeps (Rocket.UpdateState writes it): propellant availability,
    // the live throttle and the remaining burn time. A rocket of a tree without rocket states reads
    // as RocketState.Zero.
    public static RocketState GetRocketState(Vehicle vehicle, Rocket rocket)
    {
        RequireSameTree(vehicle, rocket);
        if (!ModuleStateful<Rocket, RocketState, EmptyStruct, EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var states)
            || states == null)
            return RocketState.Zero;
        return states.GetState(rocket);
    }

    // True when the engine is active and at least one of its rockets has propellant, which is the
    // flag Rocket.UpdateRockets writes into EngineControllerState. Unlike EngineHasPropellant it is
    // false for an inactive engine. The flag is written by the solver, so an engine activated
    // between steps reads as fed only after the next step.
    public static bool IsFed(Vehicle vehicle, EngineController engine)
    {
        RequireSameTree(vehicle, engine);
        if (!ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var states)
            || states == null)
            return false;
        return engine.IsActive && states.GetState(engine).IsPropellantAvailable;
    }

    // The thruster counterpart. Rocket.UpdateRockets sets ThrusterControllerState.IsPropellantAvailable
    // for an inactive thruster too, so the active flag is checked here to match the engine overload.
    public static bool IsFed(Vehicle vehicle, ThrusterController thruster)
    {
        RequireSameTree(vehicle, thruster);
        if (!ModuleStateful<ThrusterController, ThrusterControllerState, ThrusterControllerGlobalState, EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var states)
            || states == null)
            return false;
        return thruster.IsActive && states.GetState(thruster).IsPropellantAvailable;
    }

    // The live propellant mass flow in kg/s: the sum of RocketCoreState.MassFlowRate over the cores of
    // the engine's rockets. Rocket.UpdateRockets accumulates that rate from the nozzle states of the
    // last solver pass, so it follows throttle, ambient pressure and propellant starvation, unlike the
    // rated EngineController.VacuumData.MassFlowRateMax.
    public static double MassFlow(Vehicle vehicle, EngineController engine)
    {
        RequireSameTree(vehicle, engine);
        return SumCoreMassFlow(vehicle, engine.Rockets);
    }

    public static double MassFlow(Vehicle vehicle, ThrusterController thruster)
    {
        RequireSameTree(vehicle, thruster);
        return SumCoreMassFlow(vehicle, thruster.Rockets);
    }

    // The live throttle of the engine: the highest RocketState.Throttle of its rockets, 0 when none
    // fires. A solid rocket reads 1 while it burns, because SolidRocket.UpdateState sets it so.
    public static float Throttle(Vehicle vehicle, EngineController engine)
    {
        RequireSameTree(vehicle, engine);
        if (!ModuleStateful<Rocket, RocketState, EmptyStruct, EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var states)
            || states == null)
            return 0f;
        float throttle = 0f;
        foreach (Rocket rocket in engine.Rockets)
            throttle = Math.Max(throttle, states.GetState(rocket).Throttle);
        return throttle;
    }

    // Rated performance at an ambient pressure (Pa) and throttle, through the same
    // RocketControllerData.ComputeFromRockets that PartTree.RecomputeRocketControls uses for
    // VacuumData (0 Pa) and SeaLevelData (101325 Pa). The throttle is clamped per rocket to its
    // minimum throttle there. ThrustMax is the vector sum over the nozzles in the vehicle assembly
    // frame, so canted nozzles give less than the sum of their magnitudes.
    public static RocketControllerData ComputePerformance(EngineController engine, double ambientPressurePa = 0.0, float throttle = 1f)
        => RocketControllerData.ComputeFromRockets(engine.Rockets.AsSpan(), (float)ambientPressurePa, throttle);

    public static RocketControllerData ComputePerformance(ThrusterController thruster, double ambientPressurePa = 0.0, float throttle = 1f)
        => RocketControllerData.ComputeFromRockets(thruster.Rockets.AsSpan(), (float)ambientPressurePa, throttle);

    // Thrust magnitude in N of the rated performance above.
    public static double MaxThrust(EngineController engine, double ambientPressurePa = 0.0, float throttle = 1f)
        => ComputePerformance(engine, ambientPressurePa, throttle).ThrustMax.Length();

    public static double MaxThrust(ThrusterController thruster, double ambientPressurePa = 0.0, float throttle = 1f)
        => ComputePerformance(thruster, ambientPressurePa, throttle).ThrustMax.Length();

    // Specific impulse in s, from thrust over mass flow and standard gravity, as the game's engine
    // info panels show it. 0 when the rockets have no mass flow.
    public static double Isp(EngineController engine, double ambientPressurePa, float throttle = 1f)
        => IspOf(ComputePerformance(engine, ambientPressurePa, throttle));

    public static double Isp(ThrusterController thruster, double ambientPressurePa, float throttle = 1f)
        => IspOf(ComputePerformance(thruster, ambientPressurePa, throttle));

    public static double VacuumIsp(EngineController engine) => Isp(engine, 0.0);

    public static double VacuumIsp(ThrusterController thruster) => Isp(thruster, 0.0);

    // A solid rocket burns its grain once lit and ignores the throttle (SolidRocket.UpdateState), so
    // throttle and mass-flow expectations of a liquid engine do not hold for it.
    public static bool IsSolid(Rocket rocket) => rocket is SolidRocket;

    public static bool IsSolid(EngineController engine)
    {
        foreach (Rocket rocket in engine.Rockets)
        {
            if (rocket is SolidRocket)
                return true;
        }
        return false;
    }

    private static double IspOf(in RocketControllerData data)
        => data.MassFlowRateMax > 0f ? data.ThrustMax.Length() / (data.MassFlowRateMax * Constants.STANDARD_GRAVITY) : 0.0;

    // Rockets can share a core, and RocketCoreState.MassFlowRate already sums every nozzle on it, so
    // each core is counted once.
    private static double SumCoreMassFlow(Vehicle vehicle, Rocket[] rockets)
    {
        if (!ModuleStateful<RocketCore, RocketCoreState, RocketCoreGlobalState, EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var cores)
            || cores == null)
            return 0.0;
        HashSet<RocketCore> seen = new(ReferenceEqualityComparer.Instance);
        double total = 0.0;
        foreach (Rocket rocket in rockets)
        {
            foreach (RocketCore core in rocket.Cores)
            {
                if (seen.Add(core))
                    total += cores.GetState(core).MassFlowRate;
            }
        }
        return total;
    }

    // A module's StatesIdx indexes the state list of the tree that holds it. A staging split moves
    // the module into the new vehicle's tree, and reading it through the old vehicle would return the
    // state of another module without any error.
    private static void RequireSameTree(Vehicle vehicle, ModuleBase module)
    {
        if (!ReferenceEquals(module.Parent.FullPart.Tree, vehicle.Parts))
            throw new ArgumentException(
                $"[HeadlessHarness] module '{module.GetType().Name}' on part '{module.Parent.Id}' is not part of vehicle '{vehicle.Id}'.");
    }
}
