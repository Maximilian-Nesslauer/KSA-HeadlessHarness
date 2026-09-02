using KSA;

namespace HeadlessHarness.Harness;

// Deterministic fixed-step driver. Bypasses the wall-clock loop (App.Run / Program.PrepareFrame)
// by hand-building each SimStep, so stepping is reproducible on a fixed machine. Collapses the
// game's double-buffered solver pipeline into a synchronous Execute -> Wait -> Apply per stage.
//
// Not Universe.GetJobSimStep, which scales the frame delta by simulation speed and the fraction the
// solvers achieved; both would make a step depend on how fast the machine ran the previous one.
public sealed class SimDriver
{
    // Off by default: frozen celestials are fine for short vehicle tests, and cheaper.
    public bool StepOrbits { get; set; }

    // On by default, unlike StepOrbits: it costs an empty-list check without a deployed canopy, and
    // it drains the cloth acquire queue a parachute fills on deploy or deserialize. Cloth is visual
    // only, so skipping it changes the canopy pose a test can read, not the trajectory.
    public bool StepCloth { get; set; } = true;

    // Monotonic sim-time cursor. Seeded from Universe.GetElapsedTime() at construction.
    public UniverseTime Elapsed { get; private set; }

    // Created via HeadlessSession.CreateDriver so the cursor always starts at the universe's
    // current sim time; an arbitrary seed would desync SimStep times from the loaded state.
    internal SimDriver(UniverseTime start)
    {
        Elapsed = start;
    }

    public void Step(double dt)
    {
        SimStep step = new SimStep
        {
            PreviousTime = Elapsed,
            NextTime = Elapsed + dt,
            DeltaTime = dt,
        };

        // The game's activation/staging API only enqueues: EngineController.SetIsActive,
        // Decoupler.SetIsActive, and SequenceList.ActivateNextSequence all push into
        // InputEvents.IActivateInputBuffer, which only InputEvents.ApplyInputEvents drains
        // (its sole game call site is Program.PrepareFrame, the wall-clock loop this driver
        // replaces). PrepareFrame drains after the previous frame's solvers apply and before
        // the next Execute; draining at the top of Step keeps that pipeline position AND the
        // game's latency: a command issued between Steps is included in the very next solver
        // pass, like a command issued during a frame's input phase is in the running game.
        InputEvents.ApplyInputEvents();

        // Execute queues a single job on JobSystems.VehicleSolver, which is why that is the only
        // scheduler to wait on. The per-bubble fan-out over JobSystems.VehicleWorkerPool happens
        // inside that job, and again inside Apply; both batches join themselves.
        //
        // PrepareFrame also sizes the pool's spin-before-park window from the player frame time.
        // Deliberately not mirrored: dt here is SIM seconds with no wall-clock meaning, and the
        // pool's own default keeps workers hot between back-to-back steps.
        Universe.ExecuteNextVehicleSolvers(dt, step);
        JobSystems.VehicleSolver.Wait();
        Universe.ApplyVehicleSolvers();

        if (StepOrbits)
        {
            Universe.ExecuteNextOrbitSolvers(dt, step);
            JobSystems.OrbitSolvers.Wait();
            Universe.ApplyOrbitSolvers();
        }

        // Last, so canopies are posed from the vehicle state this step just produced. PrepareFrame
        // puts cloth first for the same reason: it kicks off the previous frame's applied state.
        if (StepCloth)
        {
            Universe.ExecuteNextClothSolvers(dt, step);
            JobSystems.ClothSolvers.Wait();
            Universe.ApplyClothSolvers();
        }

        Elapsed = step.NextTime;
    }

    public void Step(double dt, int count)
    {
        for (int i = 0; i < count; i++)
            Step(dt);
    }
}
