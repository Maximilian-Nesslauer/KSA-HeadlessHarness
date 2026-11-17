# Vehicle control, rules, probe power and avionics

Since KSA 2026.11.16.5901 a vehicle can lose control. After every solver apply, `Vehicle.UpdateControl` computes `Vehicle.ComputeControl(pod crew, probe core power)`: a command pod with an able kitten or a powered probe core gives control, anything else gives one of the `VehicleControl` reasons (`NoCrew`, `CrewIncapacitated`, `ProbeCoreUnpowered`, `ProbeCoreOff`, `ProbeCoreFailed`, `ProbeCoreStormStandby`, `ProbeCoreRebooting`). On the step a vehicle loses control, `Vehicle.ReleaseFlightComputer` queues its Auto attitude and burn modes back to Manual through `InputEvents.FlightComputerInputBuffer`, which the next `SimDriver.Step` drains at its top. Nothing on the solver path reads `IsControllable`, so the manual throttle stays, and a test that writes `BurnMode = Auto` after the loss keeps flying.

The helpers below live in `TestSupport` (`HeadlessHarness/Harness/TestSupport.Control.cs`) and `SimDriver` (`SimDriver.StepLimit.cs`).

## Control

| API | What it does |
| --- | --- |
| `TestSupport.GetControl(vehicle)` | The game's own verdict from the last `UpdateControl` (private `Vehicle._control`). Reads `Controllable` before the first step. |
| `TestSupport.IsControllable(vehicle)` | `vehicle.IsControllable`, including a forced answer. |
| `TestSupport.PredictControl(vehicle)` | Predicts the verdict of the next step without side effects: pod crew per habitat zone, probe core power (switch, charge, failed or rebooting avionics), then `Vehicle.ComputeControl`. Returns a `ControlExpectation` with `Expected`, `Predicted`, `Pod`, `Probe` and `Reason`. A storm standby is not predicted. |
| `TestSupport.EnsureControllable(vehicle)` | Seats crew in every free seat (`VehicleSpawner.SeatRandomCrew`), refills every battery, and returns `PredictControl` with `CrewSeated` and `BatteriesRefilled`. It does not step. |
| `TestSupport.ForceControllable(params Vehicle[])` | An `IDisposable` scope in which `Vehicle.IsControllable` answers true for these vehicles only. Scopes nest per vehicle. |

`ForceControllable` is a postfix on the `Vehicle.IsControllable` getter. `UpdateControl` reads that getter before and after it recomputes control, so a forced vehicle never sees the transition and is never released; its Auto modes stay, and the loss alert and the simulation speed reset of that transition do not happen. On dispose, a vehicle that the game holds uncontrollable gets the release once (`Vehicle.ReleaseFlightComputer`), as on the step it would have lost control, so its Auto modes go to Manual at the next step.

Limits: `GetControl`, `Vehicle.NoControlTooltip` and the probe power warning (`UpdateProbePowerWarning`) still see the real state. A `KittenEva` overrides `IsControllable` and is refused with an `ArgumentException`. The debug flag `Vehicle._overrideIsControllable` is not used: it is global and also changes `HasControlModule`, which `SolidRocket` and `PartFailure` read.

## Game rules

| API | What it does |
| --- | --- |
| `TestSupport.UseRules(radiation: null, lifeSupport: null)` | An `IDisposable` scope that sets the named rules on `Universe.Rules` and restores the values it found. Every game check reads the rules per call, so a change takes effect at the next step. |
| `TestSupport.GetRunRules()` | The rules this run chose with `KSA_HEADLESS_RULES` (with `settings`: the start rules of the profile's settings file). |
| `TestSupport.RestoreRunRules()` | Puts `Universe.Rules` back to the run's choice, for example after `Universe.DeserializeSave` replaced them with those of a save. |

## Probe power

A probe core is a `PowerConsumer` with `PowerSwitch` set on a part with a `Control` module outside any habitat zone. The stock small probe core (`CoreCommandC_Prefab_ProbeCoreSmallA`) has a 2500 J battery and draws 3 W, about 833 s.

| API | What it does |
| --- | --- |
| `TestSupport.SetProbeDraw(vehicle, watts)` | Sets `PowerConsumer.Consumed` of every probe core. |
| `TestSupport.SetProbeMode(vehicle, mode)` | Sets `PowerConsumer.Mode` (`Off`, `Auto`, `On`) of every probe core. |
| `TestSupport.ProbeForecast(vehicle)` / `ProbeForecast(vehicle, part)` | `PowerForecast.Compute` for the first probe core's circuit (or the part's): charge, draw, supply, `Remaining`, the numbers the game's warning uses. Null without a probe core. |
| `TestSupport.RefillBatteries(vehicle)` | Fills every battery with `Battery.Refill`. |
| `TestSupport.GetBatteryCharge(vehicle)` | Joules in every battery. |

A battery write goes into the part tree's state list. The solver copies that list into a fresh buffer at the start of each step and copies the result back on apply, so a write between two `SimDriver` steps, also before the first one, is what the next step starts from. `control-helpers` checks both.

## Avionics

Avionics only matter while the radiation rule is on (`Vehicle.ComputeHold`).

| API | What it does |
| --- | --- |
| `TestSupport.SetAvionicsDose(vehicle, grays)` | Sets `Avionics.DoseGy` of every avionics module. |
| `TestSupport.FailAvionics(vehicle)` | Sets the dose to `ToleranceGy`, which `Avionics.ComputeHealth` reports as `Failed`. |
| `TestSupport.TriggerReboot(vehicle)` / `TriggerReboot(vehicle, seconds)` | Sets `Avionics.RebootUntil` to now plus 30 s (`Avionics.REBOOT_SECONDS`) or the given time, as a radiation upset does. |
| `TestSupport.SeedUpsets(seed)` | An `IDisposable` scope that seeds every vehicle's upset `Random` from its id and the seed at its next roll, then restores the previous choice and random generators. Dispose nested scopes in reverse order. |
| `TestSupport.UpsetSeed` | The seed in use, or null for the game's unseeded `Random`. |

The radiation rule rolls avionics upsets in the private `Vehicle.RollUpset` from `Vehicle._upsetRandom`, which the game creates with no seed. A prefix on `RollUpset` places a `Random` seeded from the vehicle id and the run's seed first, so a radiation run repeats.

## Missions

`TestSupport.SetMission(vehicle, plan)` and `GetMission(vehicle)` set and read `PartTree.Mission`, which `Vehicle.UpdatePerFrameData` records progress on (`MissionProgress.Record`) and the life support forecast plans supplies with. The plan is stored as given; null removes it.

## Step limits

The game asks the next step not to pass a time (`Universe.LimitNextStep`) for the probe power warning and the run-out a second after it, the next life support rung, the life support forecast level and the next crew dose threshold. `Universe.GetJobSimStep` shortens a frame to that time; `SimDriver` ignores it unless asked.

| API | What it does |
| --- | --- |
| `SimDriver.NextGameStepLimit` | The time the game asked the next step not to pass (`Universe._stepLimitTime`), or null. `Universe.ApplyVehicleSolvers` clears it before the vehicles set it again, so between steps it holds the requests of the last step. |
| `driver.ClipToGameStepLimit` | Off by default. On, `Step(dt)` ends at that limit when it lies inside the step, as `GetJobSimStep` does. Loop on `driver.Elapsed`, not on a step count. |
| `driver.LastStepClipped` | Whether the last step was shortened. |

## Hygiene

`TestSupport.ClearAlerts()` empties the static `TimedAlert` list, which only the UI prunes. `TestSupport.ResetSimulationSpeed()` calls `Universe.ResetSimulationSpeed` (stops an auto warp, speed 1); a loss of control and the probe power warning set the speed to 1 as well.

## Environment variables

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_SEED` | Seed of the avionics upsets. An integer, or `off` for the game's unseeded `Random`. Unset: 0. Anything else is an infrastructure failure. |

## Example

```csharp
Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "MyTest", orbit);
using IDisposable rules = TestSupport.UseRules(radiation: true);
using IDisposable seed = TestSupport.SeedUpsets(42);
ControlExpectation control = TestSupport.EnsureControllable(vehicle);
if (!control.Expected)
    HarnessLog.Line($"[my-test] SKIP: no control ({control.Reason}).");

// A long Auto flight that must not stop because a probe core reboots.
using (TestSupport.ForceControllable(vehicle))
{
    driver.ClipToGameStepLimit = true;
    UniverseTime end = driver.Elapsed + 3600.0;
    while (driver.Elapsed < end)
        driver.Step(60.0);
}
```

`examples/HarnessConsumerExample/ExampleControlTest.cs` is a complete test.

## Self-tests

- `control-truth-table` (default): `Vehicle.ComputeControl` over every pod crew and probe core power pair against a written table, the enum sizes, and the reflection keys of this page.
- `control-force` (default): an uncrewed pod stays controllable and on Auto attitude while forced and is released on dispose; a free one is released at once.
- `control-helpers` (default): the rules scope, battery writes before and between steps, the forecast, the power switch, failed avionics and a reboot, the prediction of `EnsureControllable`, seeded upsets, missions and the hygiene helpers.
- `control-probe-power` (opt-in): a probe-only vehicle runs its battery flat in 1 s steps; the loss lands within 5 s of `PowerForecast.Remaining`, the Auto modes go to Manual one step later, the manual throttle stays. Then 600 s steps with `ClipToGameStepLimit` find the loss within 5 s as well.
- `control-reboot` (opt-in): with radiation on, a triggered reboot drops a probe-only vehicle's control for 30 s and its Auto attitude, while a crewed pod with the same probe core keeps both.

The tests build their vehicles from the stock parts `CoreCommandC_Prefab_ProbeCoreSmallA` and `CoreCommandB_Prefab_SmallLanderA` and skip when the loaded content lacks them.

## Maintenance on game update

- Patch targets, installed at bring-up by `HeadlessSession.InstallControlPatches`: a postfix on the getter of `Vehicle.IsControllable` (declared on `Vehicle`; `KittenEva` overrides it), and a prefix on the private `Vehicle.RollUpset` (Harmony injects only `__instance`).
- Reflection keys, bound on first use: `Vehicle._control` (`VehicleControl`), `Vehicle._upsetRandom` (`Random`), `Vehicle.ReleaseFlightComputer()` and the static `Universe._stepLimitTime` (`UniverseTime`). `control-truth-table` binds all four in the default run.
- Assumptions: `Vehicle.UpdateControl` reads the `IsControllable` getter before and after it writes `_control` and releases only on that transition; `ReleaseFlightComputer` releases only through the input buffer; every reader of `IsControllable` runs on the main thread. `RollUpset` creates its `Random` lazily on a null field and is the only reader. `Universe.ApplyVehicleSolvers` resets `_stepLimitTime` before `UpdatePerFrameData`, and `Universe.GetJobSimStep` clips with `next > limit && limit > previous`. `Avionics.IsRebooting` compares with `Universe.GetElapsedSeconds`. A battery state written into `PartTree.Batteries` between steps is what the next step reads (`ModuleStateful` `StateUpdater.GetNewStates` after `VehicleUpdateData.Prepare`). `GameRules` holds only `Radiation` and `LifeSupport`. The control rule table of `control-truth-table` and the enums `CommandPodCrew`, `ProbeCorePower`, `VehicleControl`.
- `PredictControl` mirrors `Vehicle.ScanControls`, `ComputeHold`, `ScanProbeCore` and the crew count of `CreditCrewStats`; a change there shows up as a failing prediction in `control-helpers`.
