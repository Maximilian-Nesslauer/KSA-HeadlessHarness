# HeadlessHarness [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A headless test harness for [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency) mods.
It brings the real game up far enough to hold a live `CelestialSystem` and `Vehicle` WITHOUT a GPU, window, or renderer, then advances the real simulation deterministically with a fixed timestep.
A mod references it as a test dependency and asserts against the real `FlightComputer`, `PhysicsStates`, and `Orbit` code, so flight and guidance logic is validated against genuine game behaviour instead of a separately maintained re-implementation that can drift out of sync.

This is a developer tool, not a gameplay mod. It is env-var gated and does nothing on a normal launch.

Written against the [StarMap loader](https://github.com/StarMapLoader/StarMap). Validated against KSA build version 2026.11.16.5901 (re-verify the bring-up on each game update, see [Maintenance](#maintenance-on-game-update)).

## How it works

- Packaged as a StarMap mod. The entry is a `[StarMapBeforeMain]` method (`Mod.OnBeforeMain`), which StarMap fires BEFORE it invokes KSA `Program.Main` - so it runs before any GLFW window or Vulkan renderer is created.
- StarMap loads the mod through its own `CoreAssemblyLoadContext`, which resolves `KSA.dll` and the `Brutal.*` native dependencies from the game folder.
- `HeadlessSession.BringUp` runs the CPU-only load calls the real `Program` constructor makes, in dependency order, skipping every GPU, window, and ImGui step. Where the game programs against an interface it supplies a CPU-only implementation instead of intercepting callers: `HeadlessViewport` is registered as the game main viewport, which is what makes `Program.MainViewport` and every camera accessor resolve. The remaining render couplings, on a few body and vehicle types and the two renderer accessors, are neutralized by a small set of Harmony patches (see [Maintenance](#maintenance-on-game-update)).
- `SimDriver` advances the vehicle, cloth, and (optionally) orbit solvers with a hand-built fixed `SimStep`, collapsing the game's double-buffered solver pipeline into a synchronous Execute -> Wait -> Apply, and drains the game's input-event queue at the top of each step. Right after the drain it runs `FlightComputer.RaisePendingAlerts` for every vehicle, as `Program.PrepareFrame` does, so a burn-end flag raised in one step is acted on at the top of the next. The game's activation APIs (`EngineController.SetIsActive`, `Decoupler.SetIsActive`, `SequenceList.ActivateNextSequence`) only enqueue, so a command issued between steps is included in the very next solver pass, with the same one-frame latency as the running game.

## What it can do

- Load a full star system GPU-free (all bodies including vehicles), with no window or renderer, chosen per run with `KSA_HEADLESS_SYSTEM`.
- Tick the real vehicle, cloth, and orbit solvers with a deterministic fixed timestep.
- Drive a vehicle through the real `FlightComputer` and `PhysicsStates` (manual throttle and engine, forced numerical physics) and read back state, mass, and orbit.
- Spawn a player-built save from the game's Vehicles folder (staged engine state, sequences, and fuel links restored) or copy a live vehicle into an arbitrary orbit, always in a fresh part tree.
- Stage a vehicle through the game's own sequence system (`SequenceList.ActivateNextSequence`): ignite engines, fire decouplers, exactly like the in-game staging key.
- Discover and run plug-in tests (`IHarnessTest`) that other mods deploy alongside it.
- Build vehicles from stock parts, resolve bundled vehicle saves, spawn at launch sites, and test EVA and boarding.
- Read rocket and gimbal state, measure aerodynamic loads without changing the live flow cache, and test probe power, reboots and control loss.
- Check content loading and part geometry, compare save round trips, and record vehicle fingerprints and solver counts.
- Run kFS programs on simulation time, write JSON and JUnit results, and bound a test with a watchdog.

## Reference

| Area | API and maintenance details |
| --- | --- |
| Vehicle construction, bundled saves, surface placement and EVA | [Spawning](docs/spawn.md) |
| Control, rules, probe power and seeded avionics | [Control](docs/control.md) |
| Engines, thrusters and gimbals | [Propulsion](docs/propulsion.md) |
| Isolated aerodynamic measurements | [Aerodynamics](docs/aero.md) |
| Bounded stepping and render stand-ins | [Stepping](docs/stepping.md) |
| Game state accessors | [Accessors](docs/accessors.md) |
| Flight programs | [kFS](docs/kfs.md) |
| Discovery, isolation, results and script parameters | [Runner](docs/runner.md) |
| Assertions, parameters and report files | [Checks](docs/checks.md) |
| Content errors and part checks | [Content](docs/content.md) |
| Fingerprints, save round trips and solver statistics | [Saves](docs/saves.md) |

## Layout

- `HeadlessHarness/Mod.cs` - StarMap `[StarMapBeforeMain]` entry point (env-var gated).
- `HeadlessHarness/Harness/HeadlessSession.cs` - GPU-free bring-up, headless viewport registration, and the render-neutralization patches.
- `HeadlessHarness/Harness/HeadlessViewport.cs` - the run's main viewport: a CPU-only `IGameViewport` with real cameras and controllers.
- `HeadlessHarness/Harness/SimDriver.cs` - deterministic fixed-step solver ticking plus input-event drain.
- `HeadlessHarness/Harness/VehicleSpawner.cs` - save/copy vehicle spawning plus orbit helpers.
- `HeadlessHarness/Harness/IHarnessTest.cs` - the plug-in test interface.
- `HeadlessHarness/Harness/TestSupport.cs` - shared helpers for tests (manual control input, propellant checks, spawn cleanup, multi-vehicle save resolution).
- `HeadlessHarness/Harness/HarnessRunner.cs` - loads consumer DLLs and discovers/runs `IHarnessTest`s.
- `HeadlessHarness/Harness/HarnessMain.cs` - the entry: bring-up, content-agnostic smoke checks, test dispatch, exit code.
- `HeadlessHarness/Harness/FlightTest.cs` - flies a player-built save like a player (burn, stage, repeat) and asserts real physics.
- `HeadlessHarness/Harness/SpawnTest.cs` / `OrbitMathTest.cs` - `IHarnessTest` self-tests that double as worked examples.
- `HeadlessHarness/Core/HarnessLog.cs` - self-contained result log (file plus console).
- `HeadlessHarness/Core/HarnessData.cs` - machine-readable CSV output beside the log, for measuring tests.
- `examples/HarnessConsumerExample/` - a complete consumer mod carrying one `IHarnessTest`.

## Environment variables

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_HARNESS=1` | Run the harness and exit before the GPU game starts. Anything else: the mod stays idle. |
| `KSA_HEADLESS_SYSTEM` | Id of the star system to load (Core: `Sol`, `SolDense`, `SolLite`, `Test`, `SolSystemInterstellar`, `EarthOnlyInterstellar`, and the test system `TestSystem`, which the game picker hides). Unset: the first system the game lists, `Sol` with Core. An unknown id or a system that failed to load is an infrastructure failure. |
| `KSA_HEADLESS_VEHICLE` | Flight-test save name, resolved from the harness bundle, consumer bundles, then the profile's Vehicles folder. Unset: the flight test skips. |
| `KSA_HEADLESS_VEHICLES` | Comma-separated save list a multi-vehicle test resolves via `TestSupport.ResolveVehicleSaves`, overriding its candidate set. Separate from the singular pin above. Unset: the test uses its own candidates. |
| `KSA_HEADLESS_TESTS` | Comma-separated test names; only matching discovered tests run, and naming an opt-in test is what runs it. A name matching nothing is an infrastructure failure (a typo must not silently skip a test). |
| `KSA_HEADLESS_RULES` | Game rules to turn on, comma-separated: `radiation`, `lifesupport`, or `settings` to keep the rules of the profile's settings file. Unset: every rule is off, so a run does not depend on the profile and has no random avionics upsets. An unknown name is an infrastructure failure. |
| `KSA_HEADLESS_LOG` | Exact path for this run's log file. Unset: a timestamp+pid file under `%TEMP%\ksa-headless-harness\`. |
| `KSA_HEADLESS_SEED` | Integer avionics seed and consumer seed, default 0 for avionics. See [Control](docs/control.md). |
| `KSA_HEADLESS_PARAMS` | Consumer parameters in `name=value;other=value` form. |
| `KSA_HEADLESS_TEST_TIMEOUT` | Default wall-clock test budget in seconds, 0 disables it. A test can override this with `TimeoutSeconds`. |
| `KSA_HEADLESS_RUN_TIMEOUT` | Whole-run wall-clock budget in seconds, 0 disables it. |
| `KSA_HEADLESS_KFS` | `1` enables the flight computer prototype for the run. |
| `KSA_HEADLESS_PART_MODS` | Comma-separated mod ids for `content-parts`, default all non-Core mods. |
| `KSA_HEADLESS_PART_DUMP` | `1` writes checked aero meshes as OBJ files next to the run log. |
| `KSA_HEADLESS_STRICT_DETERMINISM` | `1` fails the flight test when the signature differs at the same epoch. |

## Exit codes

- `0` - all tests passed.
- `1` - at least one test failed.
- `2` - infrastructure failure: bring-up, validation, discovery or cleanup failed.
- `3` - the watchdog or outer script timeout expired.
- `4` - the script's run queue timed out.
- `5` - a requested build failed.

## Concurrent runs

Multiple sessions can invoke the harness on one machine at the same time; they queue instead of conflicting:

- `scripts/run-headless.ps1` serializes the whole swap -> launch -> restore sequence through a machine-wide named mutex (`Global\KSA-HeadlessHarness-Script`), because the game manifest belongs to exactly one run at a time. A queued invocation says so on the console and waits (up to 10 minutes, then exit `4`).
- The harness itself holds a second named mutex (`Global\KSA-HeadlessHarness`) around the run, so even game processes launched directly (e.g. from the IDE) serialize. The two names are distinct on purpose: the script holds its mutex while the game runs, so a shared name would deadlock the game against its own launcher.
- Each run writes `run-<timestamp>-pid<pid>.log`, JSON results and JUnit XML under `%TEMP%\ksa-headless-harness\`. Startup keeps the newest 200 run logs and all logs younger than two days, and prunes older run logs with their side files.

## Writing a consumer test

Reference `HeadlessHarness.dll`, implement `IHarnessTest` on a public class with a parameterless constructor, deploy the DLL to a game mod folder, and declare a `HeadlessHarness` dependency in its `mod.toml`.
Once the harness is up it loads the consumer DLLs from the mods folder of the profile the game runs in (`ModLibrary.LocalModsFolderPath`) into its own load context and runs each discovered `IHarnessTest` that is not opt-in, ordered by `Name` (return 0 on pass, non-zero on failure).
A consumer DLL or test type that fails to load counts as an infrastructure failure, not a pass - that failure mode usually means the consumer references a game type that moved in an update, which is exactly what the harness exists to catch. Native (non-managed) DLLs a consumer ships alongside its assemblies are ignored.

A test that is too expensive for the normal suite (a parameter sweep, a soak run) can override `bool OptIn => true`. It then stays deployed and discoverable but runs only when `KSA_HEADLESS_TESTS` names it, so the default suite keeps its usual runtime. Every default run logs which opt-in tests it skipped, by name.

`TestSupport` carries the helpers most vehicle tests need (manual throttle input, engine propellant checks, spawn snapshot and cleanup), so a consumer does not have to duplicate the underlying game couplings; `HarnessLog` and `VehicleSpawner` are public for the same reason.

A measuring test (a calibration sweep, a benchmark) can write machine-readable output with `HarnessData`: `HarnessData.Create("<tag>", "col1,col2")` makes a CSV next to the run log (named for the run so the run script lists it), then `data.AppendRow(cell1, cell2, ...)` appends invariant-culture, CSV-escaped rows. No schema is imposed; the columns are the consumer's. `ExampleSweepTest` in the example consumer writes one.

A test that runs several saves calls `TestSupport.ResolveVehicleSaves("A", "B", ...)`: it returns the candidates that resolve in bundled saves or the profile Vehicles folder (logging a note for any that do not), or the comma-separated `KSA_HEADLESS_VEHICLES` / `run-headless.ps1 -Vehicles` list if set. Iterate the result, spawn each with a per-save log prefix, and add your own SKIP for a save that exists but is unsuitable (missing the parts the test needs). `ExampleMultiVehicleTest` in the example consumer is the worked pattern; because it flies more than one save it is opt-in (`OptIn => true`).
A test that needs a particular star system checks the loaded system (`Universe.Roots`, the bodies it needs) and logs a SKIP when it does not fit.
[`examples/HarnessConsumerExample`](examples/HarnessConsumerExample) is a complete worked example.

## Running the self-tests

`scripts/run-headless.ps1` runs the suite headless: it backs up the game manifest, swaps to a minimal `Core + HeadlessHarness` manifest, launches StarMap with `KSA_HEADLESS_HARNESS=1`, then ALWAYS restores the manifest, prints this run's log, and exits with the harness exit code (`3` = timeout, `4` = run-queue timeout, `5` = build failure). On a failed run it also archives the game's own log next to the run log.
`-InstancePath <folder>` runs the game in another profile folder, such as a launcher instance: the script passes it to StarMap as `STARMAP_INSTANCE_PATH`, so the game, the harness and every mod read and write that folder instead of `Documents\My Games\Kitten Space Agency`. The manifest swap, the vehicle saves and the consumer mods are then those of that folder, so deploy the mods under test into its `mods` folder first; a folder without a `mods` folder is an infrastructure failure (exit `2`).
`-Vehicle <save name>` selects the flight vehicle, default `Test Vehicle 1` from the bundle; `-Tests a,b` filters tests; `-System <id>` selects the system; `-Build` builds the harness and example inside the queue; `-TimeoutSec <n>` sets the outer timeout, default 900 seconds.
Use `-ContentMods a,b` to enable content mods, `-PreMods a,b` to load code mods before the harness, or `-KeepManifest` to use a dedicated profile's manifest.
`-Rules`, `-Seed`, `-Param`, `-EnvVars` and `-TestTimeoutSec` configure reproducible tests.
`-Deploy`, `-SaveSource` and `-SaveName` stage additional test inputs and restore the original profile files afterwards.
The [runner reference](docs/runner.md) describes each option and restoration behavior.
The flight test places the vehicle save around the loaded system's home body, and keeps its determinism baseline per game build, system, save, profile and rules, with the spawn epoch checked separately.
The flight test wants a working staged vehicle: build one in-game (e.g. two stages: engines in sequence 1, the decoupler in sequence 2, the upper engine in sequence 3) and save it in the Vehicles window.
With the flag unset the mod stays idle and never disrupts a normal launch.

## Build

Targets **.NET 10**. `dotnet build`, or open `HeadlessHarness.slnx` in Visual Studio.

Game DLLs are provided at runtime by StarMap's load context, so every game reference is `Private=false` (not copied into the output). The built `HeadlessHarness.dll` therefore contains no game code. Building from source needs a local KSA install; the `.csproj` HintPaths point at `refs/game` in this tree, so adjust them to your own install.

The `CopyToMods` target deploys the DLL and `mod.toml` so StarMap discovers it: into `<folder>\mods\HeadlessHarness\` when the build gets `-p:ModDeployDir=<folder>\mods\HeadlessHarness\` (as `run-headless.ps1 -Build` does), and into `Documents\My Games\Kitten Space Agency\mods\HeadlessHarness\` for a build inside Visual Studio or with `-p:DeployToMods=true`. Any other command-line build only compiles.

The example consumer builds separately (`dotnet build examples/HarnessConsumerExample`) and expects the harness to be built first, since it references the harness build output the way a real consumer references the deployed DLL.

| Package | Source | Tested version |
| --- | --- | --- |
| [StarMap.API](https://github.com/StarMapLoader/StarMap) | NuGet | 0.3.6 |
| [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) | NuGet | 2.4.2 |

## Maintenance on game update

The bring-up mirrors the game's own load sequence, supplies a CPU-only viewport, and patches a handful of render couplings by name, so every game version must be re-verified against the current game code:

Each reference page above lists the additional patch targets, reflection keys and game assumptions for its API.
Check those lists as well as the core list below.

- The load calls in `HeadlessSession.BringUp` (all public static in the verified build), including `Input.OnApplicationStart` ahead of the settings load: it registers the Tomlet mapper for `BindingValue`, which `GameSettings.TomlKeyBindings` holds a dictionary of and `LoadFromFile` writes back out. The content load is `ModLibrary.PrepareAll`, `PreloadAssetBundles`, `LoadAll` and `AssignDefaults`, then, after the job systems, `ModLibrary.AssignRandomHeightmaps`, `ModLibrary.RunPostLoad` and `CelestialTextureStreamer.LoadStreamed` with a null renderer (it reads the body heightmaps and normal maps marked `Stream="true"`, which `LoadAll` skips; without it every body is a smooth sphere), in the order of the `Program` constructor; `ModLibrary.Bind` (GPU uploads) and `ModLibrary.StartSerializerWarm` are left out. Re-check that no new CPU step was added between them.
- `HeadlessViewport`, which implements the game's `IGameViewport`. A member added to or retyped on `IViewport` / `IGameViewport` breaks the build, which is the intended signal; decide per member whether it is sim state (implement it) or a render surface (throw, like the existing four).
- The viewport registration seam in `HeadlessSession.RegisterHeadlessViewport`: `ViewportRegistry.Allocate`, `ViewportRegistry.Register(IViewport)`, the `ViewportRegistry._mainViewport` field, and the `Id` / `ShaderSlot` members of the internal `ViewportAllocation`. The run aborts if any is missing, and the smoke check re-asserts that `Program.MainViewport` and `Program.GetMainCamera()` resolve to it.
- The Harmony patch targets: `Universe.OnLoaded`, the `DistantSphereRenderer` and `KittenRenderable` constructors, `KittenRenderable.Dispose` (`Vehicle.Dispose` calls it for every Kitten EVA, and the skipped constructor leaves its avatar null), `Program.GetOceanRenderer`, `Program.GetPlanetRenderer` (the ground clutter collision sync reaches it from the solver path; null headless, so clutter colliders are always cleared and a surface test never sees a clutter collision), `Decoupler.Decouple` (headless split without audio/particles), and the transpiler on `Vehicle.Teleport`, which skips its closing `Program.Instance.SchedulePlanetUpdate` call when there is no `Program` (the run aborts if the call is not found exactly once).
- The body of the `Decoupler.Decouple` stand-in, which replaces the stock method outright: it must keep resolving the vehicle to split the same way stock does. Stock changes here are invisible to the compiler and show up only as wrong staging results.
- `VehicleSpawner.SpawnFromSave`, which restores a save the way `VehicleTemplate.CreateInto` does (the overload with `applyTankAssignments` also runs `PartTree.ApplyTankAssignments`, as `VehicleLaunchMenu.LoadPreview` does before a launch) but builds the sequence list (`PartTree.EnsureDerived(DerivedData.Sequences)`) before `SequenceList.SetActiveSequence`, because `SequenceList.Add` advances the active sequence for each sequence it adds at or below it. Re-check that the list is still built lazily and that `Add` still behaves this way.
- The system selection: the `System` templates are registered by the preload, so `HeadlessSession` resolves the start system there with `SystemLibrary.Find` and `SystemLibrary.PickerSystems` (unnamed: the first listed system, as `SystemLibrary.ChooseStart` picks without a last system), sets `SystemLibrary.Default`, and re-checks after `LoadAll`, because `ModLibrary.LoadFrom` reselects `Default` by id and falls back to another system with only a log line. Re-check that the full load still loads every system, not only the selected one.
- `Loading.Current` stays null headless. `Loading.Task`, `Loading.State`, `Loading.PopTask` and `LoadTask` accept that; if a load path starts to require a loading screen, the bring-up needs a stand-in again.
- `Universe.LoadSystem` sets `Universe.Rules` (radiation, life support) from the local game settings; the bring-up overrides them with `KSA_HEADLESS_RULES` (off by default). Re-check that `GameRules` still holds only these two rules and that every check reads them per call. The prefix on `Vehicle.RollUpset` seeds avionics upsets from vehicle id and run seed, default 0; `KSA_HEADLESS_SEED=off` keeps the game source. `CelestialSystem` creates the system vehicles only when the settings start type is `Testing`. A test that depends on either reads the loaded state.
- `Universe.WorldSun` stays the first `StellarBody` headless, because only the render path moves it (`Universe.UpdateWorldSun`). Sim code reads `Universe.NearestStar` and is unaffected.
- The reflection keys: `Vehicle._manualControlInputs`, `Vehicle._threadWorkerUpdateState` (the vehicle's physics bubble, which the game exposes only as the `Vehicle.HasPhysicsBubble` flag), and the internal `PartTree.FlushDirtyResourceManagers`.
- The `Program.IsControlledVehicleActive = false` stand-in: `Vehicle.PrepareWorker` reads `ImGui.GetIO()` for the controlled vehicle while it is true, and there is no ImGui context headless. Re-check whether the game still guards that read the same way, and whether the `ClearHeldPlayerInput` it runs instead still leaves the manual throttle alone.
- The `SimDriver.Step` pipeline, which mirrors `Program.PrepareFrame`: `KfsIntegration.BeforeInputEvents` and then the `InputEvents.ApplyInputEvents` drain sit in the same position (after the solvers apply, before the next execute), followed by `FlightComputer.RaisePendingAlerts` for every vehicle in `Program.VehiclesInFrame`, then `PartTree.FlushDirtyDerived` and `PartTree.FlushDirtyResourceManagers` before the vehicle execute, as in `PrepareFrame`. `Step` runs the two flushes again at its end, so a test reading between steps sees the part trees rebuilt the way the game UI does. The three solver stages plus the schedulers they wait on (`JobSystems.VehicleSolver`, `JobSystems.OrbitSolvers`, `JobSystems.ClothSolvers`) must still be the ones the game frame drives. A stage the game adds belongs here too. Orbit results apply before vehicle results so the frame-cache refresh in `Universe.ApplyVehicleSolvers` sees the new orbit time. Cloth runs last to pose canopies from the vehicle state just produced. `PrepareFrame` also sizes the vehicle worker pool's spin-before-park window from the player frame time; the driver deliberately leaves that at the pool default, because its `dt` is sim seconds with no wall-clock meaning. It also leaves out the `JobSystems.NearestOrbitAndPerformanceWorker` wait and the renderer present wait, because only `Program` queues UI and editor jobs on that worker and headless there is nothing to present. Re-check that skipping these is still harmless.
- The aerodynamics build: `PartTree.FlushDirtyDerived` starts it on `JobSystems.VehicleWorkerPool` (`PartTree.RefreshAerodynamics`), and no solver wait joins it, so `SimDriver` calls `PartTree.AwaitAerodynamics` on every vehicle after each flush. Without that, the panel width a later build reuses (`AerodynamicsBuilder.FindReusableWidth`) depends on thread timing. Re-check that the build still starts there and that awaiting it is still all it takes to publish it.
- `TestSupport.EngineHasPropellant` reads the per-rocket `RocketState` list (`ModuleStateful<Rocket, RocketState, EmptyStruct, EmptyStruct>.TryGetFrom`), the way `Part.DrawPartInfo` does.
- Crew and control: `Vehicle.ComputeControl` gives a crew pod with no able crew and no powered probe core no control, and `Vehicle.ReleaseFlightComputer` then queues the auto modes back to Manual. `VehicleSpawner.SeatRandomCrew` clears every seat that names a kitten not crewing the vehicle (a save's own roster, the source of a copy) and fills the free seats from the run's roster; `SpawnCopy` seats crew of its own. Re-check the control rules on each update.
- Whether the game grew a sim path that needs a render object the harness does not supply. The rule is to hand the game a CPU-only implementation where it programs against an interface (as `HeadlessViewport` does), and to patch the accessor only where it does not.

The run stops with a clear "game version may have changed" error before any test starts if a patch target or reflection key is missing (the session check binds `PartTree.FlushDirtyResourceManagers` when it creates the first `SimDriver`), and the diagnostic finalizers on `CelestialSystem.CreateTreeFrom` / `CreateTreeFromRoot` log any body-construction exception. Harmony injects their `node` / `rootNode` parameters (`SystemLayout.Node`) by name, so a renamed parameter fails the patch at bring-up. Update `TestedGameVersion` in `Mod.cs` after re-verifying against a new build.

## License

MIT - see [LICENSE](LICENSE).
