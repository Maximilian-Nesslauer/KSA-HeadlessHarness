using KSA;

namespace HeadlessHarness.Harness;

// Deterministic fixed-step driver. Bypasses the wall-clock loop (App.Run / Program.PrepareFrame)
// by hand-building each SimStep, so stepping is reproducible on a fixed machine. Collapses the
// game's double-buffered solver pipeline into a synchronous Execute -> Wait -> Apply per step.
public sealed class SimDriver
{
    // Also advance celestial motion (orbit solvers) each step. Off by default: celestials are
    // effectively frozen, which is fine for short vehicle tests and cheaper.
    public bool StepOrbits { get; set; }

    // Monotonic sim-time cursor. Seeded from Universe.GetElapsedSimTime() at construction.
    public SimTime Elapsed { get; private set; }

    // Created via HeadlessSession.CreateDriver so the cursor always starts at the universe's
    // current sim time; an arbitrary seed would desync SimStep times from the loaded state.
    internal SimDriver(SimTime start)
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

        Universe.ExecuteNextVehicleSolvers(dt, step);
        JobSystems.VehicleSolvers.Wait();
        Universe.ApplyVehicleSolvers();

        if (StepOrbits)
        {
            Universe.ExecuteNextOrbitSolvers(dt, step);
            JobSystems.OrbitSolvers.Wait();
            Universe.ApplyOrbitSolvers();
        }

        Elapsed = step.NextTime;
    }

    public void Step(double dt, int count)
    {
        for (int i = 0; i < count; i++)
            Step(dt);
    }
}
