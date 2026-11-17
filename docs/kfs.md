# kFS

kFS is the game's prototype of programmable vehicle computers (namespace `KSA.Kfs`): kC and kASM programs that run on a virtual machine per vehicle and fly it instead of, or through, the stock flight computer. It needs no renderer, so the harness runs it headless, and `Kfs` loads a program onto a vehicle the way the debug console does (`kfs load <file> --vehicle`, `kfs start`, `kfs stop`).

## How it runs headless

- The vehicle computers run on the solver path: `PhysicsBubble.RunFlightComputer` calls `KfsDispatch.TryComputeControl`, which dispatches the vehicle's `KfsVehicleHost` while `KfsPrototype.Enabled` is true. `SimDriver.Step` runs `KfsIntegration.BeforeInputEvents` (main-thread services of every host, and the release of hosts whose vehicle is gone) where `Program.PrepareFrame` does.
- A computer ticks on a grid of sim time: `VehicleComputer.TryStart` aligns it to the 10 ms lattice of `TickGrid`, and the solver steps to each tick (`PhysicsStepLimit.KfsTick`). The only wall-clock check is the tick deadline (`KfsVehicleHost.TickDeadlineAt`), which is off while `KfsVehicleHost.FrameBudgetMs` is 0. Only `KfsIntegration.RegisterCommands`, which needs a `Program`, sets a budget, and the bring-up sets it to 0. So the ticks depend on sim time alone, and two flights from the same state end bit for bit the same.
- `KfsIntegration.Output` (what kFS prints: build reports, console verbs) goes to the harness log with the prefix `[kfs]`; each loaded program prints with `[kfs <vehicle id>]`. The messages a program logs on its computer are in `KfsProgram.ConsoleLines()`.

## Environment variable

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_KFS=1` | Turns the prototype on at bring-up. Anything else turns it off, also when the game's own `KFS_PROTOTYPE=1` would have turned it on, so a run does not depend on the shell it started from. A test can turn it on for itself with `Kfs.Enable()` instead. |

## API

| Member | What it does |
| --- | --- |
| `Kfs.Enabled` | `KfsPrototype.Enabled`. |
| `Kfs.SetEnabled(bool)` | `kfs on` or `kfs off` through `KfsConsoleCommands.Execute`; turning it off hands every vehicle back to the stock flight computer, as the console does. |
| `IDisposable Kfs.Enable()` | Turns it on and returns a scope whose `Dispose` restores the state it found. |
| `Kfs.ExampleFiles` | The workspace paths of the example programs the game ships (`KSA.Kfs.Host.Workbench.Examples.Files`); the constants of `Examples` (for example `Examples.KC_ATTITUDE`) name them. |
| `KfsProgram Kfs.LoadExample(Vehicle, string example)` | Writes the files of an example project into a workspace of its own and loads it onto the vehicle. `example` is a key of `ExampleFiles` or an example name such as `kC_attitude_control`. |
| `KfsProgram Kfs.LoadSource(Vehicle, string file, string source)` | Loads a program from source text. `file` is a relative workspace path without spaces; its extension picks the language (`.c` kC, `.kasm` kASM) and its folder the project. |
| `Kfs.ReleaseDeadHosts()` | The internal `KfsVehicleHost.ReleaseDead`: drops the hosts of vehicles that are gone. Each step with kFS on does it too. |
| `Kfs.DetachAll()` | Takes the computer off every vehicle of the loaded system and drops all hosts, so the next test starts without kFS state. A detached vehicle flies on the stock flight computer at once, without the hand-back that a stop runs at the next step. |
| `KfsProgram.Start()`, `Stop()` | Queue `kfs start` or `kfs stop` for the vehicle's computer, which takes the command at the next step. A stop also hands the vehicle back to the stock flight computer. |
| `KfsProgram.UnderKfsControl`, `State`, `Computer`, `Host`, `Workbench`, `Build` | The computer's state, the `VehicleComputer`, the `KfsVehicleHost`, the `Workbench` that loaded it and the loaded `AssembleOutcome` (diagnostics, code size, compile report). |
| `KfsProgram.ConsoleLines()`, `LogConsole()` | What the program and its host logged on the computer's console, as a list or into the harness log. |
| `KfsProgram.Dispose()` | Takes the computer off the vehicle at once, drops its host and deletes the workspace. |

Loading needs kFS on and throws `InvalidOperationException` with the build diagnostics when the program does not build; a failed load leaves no host behind. A vehicle has one computer: a second load onto the same vehicle replaces the image on it, and disposing either program takes the computer off the vehicle, so keep one program per vehicle at a time. Each program has its own `Workbench`, whose active vehicle is the vehicle it was loaded onto, and its own workspace folder under `%TEMP%\ksa-headless-harness\kfs\`, so nothing is written to the profile's `kfs` folder and the console's workbench (`KfsIntegration.Workbench`, which targets `Program.ControlledVehicle`) is never created.

Static state that outlives a test: the host list (`KfsVehicleHost.AllHosts`), which `DetachAll` empties, and the fault counter (`KfsVehicleHost.FaultCount`, `LastFault`), which only grows, so a test compares it before and after. The tick counter of the frame budget (`KfsVehicleHost.BeginFrame`) has no effect while the budget is off.

## Example

```csharp
using IDisposable kfs = Kfs.Enable();
bool offRails = PhysicsBubble._forceOffRails;
PhysicsBubble._forceOffRails = true;
try
{
    using KfsProgram program = Kfs.LoadExample(vehicle, Examples.KC_ATTITUDE);
    program.Start();
    if (!driver.StepUntil(() => program.UnderKfsControl, 0.1, 5.0, "kFS did not start"))
        return 1;
    driver.StepFor(120.0, 0.1);
    program.Stop();
    driver.Step(0.1);
}
finally
{
    PhysicsBubble._forceOffRails = offRails;
    Kfs.DetachAll();
}
```

## Self-test

`kfs-prograde-hold` (opt-in) flies the example "prograde hold" (`Examples.KC_ATTITUDE`, a kC program in full control that points the nose along the velocity with the RCS thrusters) on the flight-test vehicle in a 500 km circular orbit for 300 s of sim time. The pointing error must fall below 2 degrees and stay there over the last 60 s, a stop must hand the vehicle back within one step, and two flights from the same state must end bit for bit the same (position, velocity, attitude, rates, mass and the tick count). It turns kFS on for itself and restores the switch.

## Maintenance on game update

kFS is a prototype and changes on most builds; re-verify all of this against the decompiled source.

- Bring-up: `KfsPrototype.Enabled` (and that the game still reads `KFS_PROTOTYPE` in its static initializer), `KfsIntegration.Output`, `KfsVehicleHost.FrameBudgetMs` and that only `KfsIntegration.RegisterCommands` sets a budget. If the tick deadline starts to depend on anything else than the budget, the ticks are no longer deterministic.
- The dispatch path: `PhysicsBubble.RunFlightComputer` to `KfsDispatch.TryComputeControl` to `KfsVehicleHost.Dispatch`, and `KfsIntegration.BeforeInputEvents` in `Program.PrepareFrame` before `InputEvents.ApplyInputEvents` (mirrored in `SimDriver.Step`).
- The console verbs `kfs on` and `kfs off` (`KfsConsoleCommands.Execute`); `Kfs.SetEnabled` throws the drift error when the switch did not change.
- The load path of the console: `Workbench(GameTables, string, TextWriter)`, `Workbench.ActiveVehicle`, `Workbench.ActiveVehicleIdSource`, `Workbench.Load("<file> --vehicle")` (arguments split at spaces), `Workbench.Start`, `Workbench.Stop`, `Workbench.LastBuild`, `Workspace.Write`, `Workspace.FolderOf`, `Examples.Files`, `Examples.MainFile` and `Examples.Write(Workspace, string, bool)`.
- `KfsVehicleHost.For`, `KfsVehicleHost.Find`, `Vehicle.KfsHost` and `KfsVehicleHost.IsAlive` (a host is dead once its vehicle no longer points at it), and the reflection key: the internal static `KfsVehicleHost.ReleaseDead()`.
- `VehicleComputer.UnderKfsControl`, `HostComputer.State`, `HostComputer.LastK`, `HostComputer.Console` and `ConsoleLog.Snapshot`.
- The example `kC_attitude_control/prograde_hold.c` and its constant `Examples.KC_ATTITUDE`; the self-test depends on what it does (full control, RCS only, nose along the CCI velocity).
