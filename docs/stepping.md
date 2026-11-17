# Stepping

`SimDriver` advances the simulation by one fixed step (`Step(dt)`). The helpers on this page replace the loops a test otherwise writes around it, and the render stand-ins keep a step from failing when the solver raises a render event.

## API

| Member | What it does |
| --- | --- |
| `bool StepUntil(Func<bool> condition, double dt, double maxSimSeconds)` | Steps until `condition` returns true and returns true. Returns false once `maxSimSeconds` of sim time passed without it, and writes one `[stepping] StepUntil timed out ...` line. The condition is checked before the first step (a condition that already holds takes no step) and after every step. |
| `bool StepUntil(..., string message)` | The same; `message` names what the test waited for in the timeout line. |
| `bool StepUntil(..., Func<string>? describe)` | The same; `describe` runs only on a timeout, so it can format the state at that moment. |
| `void StepFor(double simSeconds, double dt, Action? perStep = null)` | Steps for `simSeconds` of sim time in steps of `dt`. `perStep` runs after every step. |
| `HeadlessSession.SkippedGroundImpactCount`, `SkippedWaterSplashCount` | How often a stand-in below took a ground impact or a water splash. Read the difference across a drop to prove the event fired. |

Both loops measure the span on `SimDriver.Elapsed`, not by counting steps. `UniverseTime` counts whole nanoseconds, so every step rounds `dt`; the last step takes what is left of the span (shorter than `dt`, or by that rounding a few nanoseconds longer), so a span ends on the nanosecond: `StepFor(2.5, 1.0)` takes steps of 1, 1 and 0.5 s, and `StepFor(1.0, 1.0 / 3.0)` takes three steps that end exactly 1 s later. A step that does not advance the cursor throws `InvalidOperationException`, so a loop cannot spin forever. `dt` must be positive and finite, a span zero or positive and finite (`ArgumentOutOfRangeException` otherwise).

## Example

```csharp
SimDriver driver = session.CreateDriver();
if (!driver.StepUntil(() => vehicle.Situation.HasAnyContact(), 0.02, 30.0,
        () => $"no touchdown, {vehicle.GetRadarAltitude():F1} m above the ground"))
    return 1;
double peak = 0.0;
driver.StepFor(5.0, 0.02, () => peak = Math.Max(peak, vehicle.AccelerationBody.Length()));
```

`examples/HarnessConsumerExample/ExampleSteppingTest.cs` is a complete test.

## Render stand-ins

`Universe.ApplyVehicleSolvers` hands the render events of each vehicle to the main thread (`PhysicsBubble.ApplyRenderEventsToVehicles`). Headless there is no `Program`, so every event whose `Apply` reaches `Program.Instance` must be stood in for. The harness installs these at bring-up; a consumer test does not patch them itself.

| Sim path | Game call | Headless |
| --- | --- | --- |
| Touchdown on terrain (`ConstraintSim.DetectTerrainContact` raises a `GroundImpactEvent`, at least 50 J) | `Celestial.TrySpawnGroundImpact` starts dust emitters through `Program.Instance.ParticleSystem` | Skipped by a prefix and counted in `SkippedGroundImpactCount`. Particles only, so nothing on the sim path changes. |
| Entry into the sea (`PhysicsBubble.DetectWaterSplash` raises a `WaterSplashEvent`) | `Celestial.TrySpawnWaterSplash`, the same particle system | Skipped and counted in `SkippedWaterSplashCount`. |
| Separation (`Decoupler.Decouple`) | sound and particles through `Program.Instance` | Replaced by the split alone (`HeadlessSession.HeadlessDecouple`, see the README). |
| Set orbit (`Vehicle.Teleport`) | `Program.Instance.SchedulePlanetUpdate` | The call is skipped without a `Program` (see the README). |
| Ground clutter collision sync, radar altitude | `Program.GetPlanetRenderer`, `Program.GetOceanRenderer` | Both return null (see the README). |
| Destruction (`VehicleDestructionEvent` to `Universe.DestroyVehicleFromEvent`) and part failure (`PartFailureEvent`) | `ExplosionSystem.SpawnForDestruction` | No stand-in needed: it only queues a request on the main thread, and only `Program` drains the queue (`ExplosionSystem.Update`). Debris (`PartFailure.ShedDebris`) is pure sim. The camera hand-off walks the headless viewport's cameras. |
| Docking (`DockingEvent`) | `Program.MainViewport.GetCamera()`, `Program.GetOrbitController()` | No stand-in needed: the headless viewport supplies both. |
| Parachute cloth (`ChuteClipAttractors.EnsureLoaded`) | `Program.Instance.SuperMeshRenderSystem` | No stand-in needed: the game catches the exception, logs one warning and poses the canopy procedurally. |
| Exhaust, ice and insulation particles (`Vehicle.SpawnParticles`) | `Program.Instance.ParticleSystem` | Not on the sim path: only `Program` calls it per frame. |
| Explosion presets (`ExplosionSystem.SpawnPreset`) | `Program.Instance` | Not on the sim path: only the `explode` console command and the explosion editor call it. |
| Loading a universe save (`Universe.DeserializeSave`) | `Program.Instance.ParticleSystem` | The harness supplies the save-path transpilers described in [Saves](saves.md). |

A vehicle that was moved with `Vehicle.Teleport` suppresses its impact and splash effects for 5 s of player time (`Vehicle.IsImpactFxSuppressed`). Headless the player time (`Program.GetPlayerTime`) never advances, so such a vehicle never raises them again; spawn a vehicle where it should fall instead of teleporting it there when a test counts these events.

The opt-in self-test `standins` drops the flight-test vehicle onto dry ground and into the sea of the home body and passes when both events reached a stand-in and the steps ran on without an exception.

## Self-tests

- `stepping-until` (default): the loops above on the loaded system, without a vehicle of its own, including the timeout, the shortened last step and that `Universe.GetElapsedTime` follows the driver.
- `standins` (opt-in): see above.

## Maintenance on game update

- Patch targets: `Celestial.TrySpawnGroundImpact(GroundImpactEvent)` and `Celestial.TrySpawnWaterSplash(WaterSplashEvent)`, both with a skip prefix. The run aborts at bring-up if either is missing. Re-check that they still only spawn particles, and that `GroundImpactEvent.Apply` and `WaterSplashEvent.Apply` are still their only callers.
- The render event list above: re-read `PhysicsBubble.ApplyRenderEventsToVehicles` and every `IVehicleRenderEvent.Apply`, and grep the vehicle solver path (`PhysicsBubble`, `ConstraintSim`, `VehicleUpdateState`, `FlightComputer`, `ChuteClothSystem`, `PartFailure`, `DockingPort`, `ExplosionSystem`) for `Program.Instance` and the renderer accessors. A new event or call that throws headless needs a stand-in here.
- `ExplosionSystem.SpawnForDestruction` must still only queue. If it starts to spawn at once, it needs a stand-in.
- `Vehicle.IsImpactFxSuppressed` and `Program.GetPlayerTime`, for the note on teleported vehicles.
- `SimDriver.StepUntil` and `StepFor` assume that `UniverseTime` is a whole number of nanoseconds and that `UniverseTime + double` rounds to the nearest one (`UniverseTime.NanosecondsFromSeconds`).
