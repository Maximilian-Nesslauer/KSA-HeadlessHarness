# HeadlessHarness [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A headless test harness for [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency) mods.
It brings the real game up far enough to hold a live `CelestialSystem` and `Vehicle` WITHOUT a GPU, window, or renderer, then advances the real simulation deterministically with a fixed timestep.
A mod references it as a test dependency and asserts against the real `FlightComputer`, `PhysicsStates`, and `Orbit` code, so flight and guidance logic is validated against genuine game behaviour instead of a separately maintained re-implementation that can drift out of sync.

This is a developer tool, not a gameplay mod. It is env-var gated and does nothing on a normal launch.

Written against the [StarMap loader](https://github.com/StarMapLoader/StarMap). Validated against KSA build version 2026.7.6.4939 (re-verify the bring-up on each game update, see [Maintenance](#maintenance-on-game-update)).

## How it works

- Packaged as a StarMap mod. The entry is a `[StarMapBeforeMain]` method (`Mod.OnBeforeMain`), which StarMap fires BEFORE it invokes KSA `Program.Main` - so it runs before any GLFW window or Vulkan renderer is created.
- StarMap loads the mod through its own `GameAssemblyLoadContext`, which resolves `KSA.dll` and the `Brutal.*` native dependencies from the game folder.
- `HeadlessSession.BringUp` runs the CPU-only load calls the real `Program` constructor makes, in dependency order, skipping every GPU, window, and ImGui step, and installs a small set of Harmony patches that neutralize the render couplings a few body and vehicle types have (see [Maintenance](#maintenance-on-game-update)).
- `SimDriver` advances the vehicle (and optionally orbit) solvers with a hand-built fixed `SimStep`, collapsing the game's double-buffered solver pipeline into a synchronous Execute -> Wait -> Apply, and drains the game's input-event queue at the top of each step. The game's activation APIs (`EngineController.SetIsActive`, `Decoupler.SetIsActive`, `SequenceList.ActivateNextSequence`) only enqueue, so a command issued between steps is included in the very next solver pass, with the same one-frame latency as the running game.

## What it can do

- Load a full star system GPU-free (all bodies including vehicles), with no window or renderer.
- Tick the real vehicle and orbit solvers with a deterministic fixed timestep.
- Drive a vehicle through the real `FlightComputer` and `PhysicsStates` (manual throttle and engine, forced numerical physics) and read back state, mass, and orbit.
- Spawn a player-built save from the game's Vehicles folder (staged engine state, sequences, and fuel links restored) or copy a live vehicle into an arbitrary orbit, always in a fresh part tree.
- Stage a vehicle through the game's own sequence system (`SequenceList.ActivateNextSequence`): ignite engines, fire decouplers, exactly like the in-game staging key.
- Discover and run plug-in tests (`IHarnessTest`) that other mods deploy alongside it.

## Layout

- `HeadlessHarness/Mod.cs` - StarMap `[StarMapBeforeMain]` entry point (env-var gated).
- `HeadlessHarness/Harness/HeadlessSession.cs` - GPU-free bring-up plus the render-neutralization patches.
- `HeadlessHarness/Harness/SimDriver.cs` - deterministic fixed-step solver ticking plus input-event drain.
- `HeadlessHarness/Harness/VehicleSpawner.cs` - save/copy vehicle spawning plus orbit helpers.
- `HeadlessHarness/Harness/IHarnessTest.cs` - the plug-in test interface.
- `HeadlessHarness/Harness/HarnessRunner.cs` - loads consumer DLLs and discovers/runs `IHarnessTest`s.
- `HeadlessHarness/Harness/HarnessMain.cs` - the entry: bring-up, content-agnostic smoke checks, test dispatch, exit code.
- `HeadlessHarness/Harness/FlightTest.cs` - flies a player-built save like a player (burn, stage, repeat) and asserts real physics.
- `HeadlessHarness/Harness/SpawnTest.cs` / `OrbitMathTest.cs` - `IHarnessTest` self-tests that double as worked examples.
- `HeadlessHarness/Core/HarnessLog.cs` - self-contained result log (file plus console).
- `examples/HarnessConsumerExample/` - a complete consumer mod carrying one `IHarnessTest`.

## Environment variables

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_HARNESS=1` | Run the harness and exit before the GPU game starts. Anything else: the mod stays idle. |
| `KSA_HEADLESS_VEHICLE` | Name of a save under `Documents\My Games\Kitten Space Agency\Vehicles` for the flight test. Unset: the flight test skips. |
| `KSA_HEADLESS_TESTS` | Comma-separated test names; only matching discovered tests run. A name matching nothing is an infrastructure failure (a typo must not silently skip a test). |

## Exit codes

- `0` - all tests passed.
- `1` - at least one test failed.
- `2` - infrastructure failure: bring-up broke, the session did not validate, or a consumer assembly failed to load. Test results are not trustworthy.

## Writing a consumer test

Reference `HeadlessHarness.dll`, implement `IHarnessTest` on a public class with a parameterless constructor, deploy the DLL to a game mod folder, and declare a `HeadlessHarness` dependency in its `mod.toml`.
Once the harness is up it loads consumer DLLs into its own load context and runs every discovered `IHarnessTest`, ordered by `Name` (return 0 on pass, non-zero on failure).
A consumer DLL or test type that fails to load counts as an infrastructure failure, not a pass - that failure mode usually means the consumer references a game type that moved in an update, which is exactly what the harness exists to catch. Native (non-managed) DLLs a consumer ships alongside its assemblies are ignored.
[`examples/HarnessConsumerExample`](examples/HarnessConsumerExample) is a complete worked example.

## Running the self-tests

`scripts/run-headless.ps1` runs the suite headless: it backs up the game manifest, swaps to a minimal `Core + HeadlessHarness` manifest, launches StarMap with `KSA_HEADLESS_HARNESS=1`, then ALWAYS restores the manifest, prints the harness log (`%TEMP%\ksa-headless-harness.log`), and exits with the harness exit code (`3` = timeout).
`-Vehicle <save name>` selects the flight-test vehicle (the default `Test Vehicle 1` is used only if that save exists, otherwise the flight test skips); `-Tests a,b` filters tests.
The flight test wants a working staged vehicle: build one in-game (e.g. two stages: engines in sequence 1, the decoupler in sequence 2, the upper engine in sequence 3) and save it in the Vehicles window.
With the flag unset the mod stays idle and never disrupts a normal launch.

## Build

Targets **.NET 10**. `dotnet build`, or open `HeadlessHarness.slnx` in Visual Studio.

Game DLLs are provided at runtime by StarMap's load context, so every game reference is `Private=false` (not copied into the output). The built `HeadlessHarness.dll` therefore contains no game code. Building from source needs a local KSA install; the `.csproj` HintPaths point at `refs/game` in this tree, so adjust them to your own install.

The `CopyToMods` target deploys the DLL and `mod.toml` to `Documents\My Games\Kitten Space Agency\mods\HeadlessHarness\` so StarMap discovers it.

The example consumer builds separately (`dotnet build examples/HarnessConsumerExample`) and expects the harness to be built first, since it references the harness build output the way a real consumer references the deployed DLL.

| Package | Source | Tested version |
| --- | --- | --- |
| [StarMap.API](https://github.com/StarMapLoader/StarMap) | NuGet | 0.3.6 |
| [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) | NuGet | 2.4.2 |

## Maintenance on game update

The bring-up mirrors the game's own load sequence and patches a handful of render couplings by name, so every game version must be re-verified against the current game code:

- The load calls in `HeadlessSession.BringUp` (all public static in the verified build).
- The Harmony patch targets: `Universe.OnLoaded`, the `Loading` screen stand-in, the `DistantSphereRenderer` and `KittenRenderable` constructors, `Program.GetOceanRenderer`, `Program.GetMainCamera`, and `Decoupler.Decouple` (headless split without audio/particles).
- The reflection keys: `Vehicle._manualControlInputs` and `Loading._tasks`.
- The input-queue drain: `InputEvents.ApplyInputEvents` in `SimDriver.Step` mirrors its position in `Program.PrepareFrame` (after the solvers apply, before the next execute).

The bring-up throws a clear "game version may have changed" error if a patch target or reflection key is missing, and the diagnostic finalizers on `CelestialSystem.CreateTreeFrom` / `CreateTreeFromRoot` log any body-construction exception. Update `TestedGameVersion` in `Mod.cs` after re-verifying against a new build.

## License

MIT - see [LICENSE](LICENSE).
