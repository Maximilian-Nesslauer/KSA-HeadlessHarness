# Propulsion

Helpers in `TestSupport` that read the rockets, engines, thrusters and gimbals of a vehicle, so a consumer does not walk the game's module state lists itself.
Live values come from the part tree's state lists, which the solver writes during a step: read them between `SimDriver` steps only.
Every helper that takes a `Vehicle` checks that the module belongs to that vehicle's part tree and throws `ArgumentException` otherwise, because after a staging split a module moves into the new vehicle's tree and reading it through the old vehicle would silently return the state of another module.

## Rockets and engines

| Member | Returns |
| --- | --- |
| `GetRocketState(Vehicle, Rocket)` | The solver's `RocketState` of one rocket: propellant availability, live throttle, remaining burn time. `RocketState.Zero` for a tree without rockets. |
| `IsFed(Vehicle, EngineController)` | True when the engine is active and one of its rockets has propellant (the `EngineControllerState.IsPropellantAvailable` flag). False for an inactive engine, unlike `EngineHasPropellant`. Written by the solver, so an engine activated between steps reads as fed after the next step. |
| `IsFed(Vehicle, ThrusterController)` | The same for an RCS thruster; the active flag is checked here because the game sets the thruster flag for inactive thrusters too. |
| `MassFlow(Vehicle, EngineController)` / `(Vehicle, ThrusterController)` | Live propellant mass flow in kg/s: the sum of `RocketCoreState.MassFlowRate` over the cores of the rockets, each core once. Follows throttle, ambient pressure and starvation. |
| `Throttle(Vehicle, EngineController)` | Live throttle 0..1, the highest `RocketState.Throttle` of the engine's rockets. A burning solid motor reads 1. |
| `ComputePerformance(EngineController or ThrusterController, ambientPressurePa = 0, throttle = 1)` | Rated `RocketControllerData` (thrust vector, mass flow, minimum throttle) through `RocketControllerData.ComputeFromRockets`, the call that fills `VacuumData` (0 Pa) and `SeaLevelData` (101325 Pa). The throttle is clamped to each rocket's minimum throttle. |
| `MaxThrust(..., ambientPressurePa = 0, throttle = 1)` | Thrust magnitude in N of that performance. It is the magnitude of the vector sum over the nozzles, so canted nozzles give less than the sum of their magnitudes. |
| `Isp(..., ambientPressurePa, throttle = 1)`, `VacuumIsp(...)` | Specific impulse in s, thrust over mass flow and `Constants.STANDARD_GRAVITY`, as the game's engine panels show it. 0 without mass flow. |
| `IsSolid(Rocket)`, `IsSolid(EngineController)` | Whether a rocket is a `SolidRocket`. A solid motor ignores the throttle and burns its grain's thrust curve, so liquid-engine expectations do not hold for it. |

`EngineHasPropellant` and `AnyActiveEngineFed` are unchanged.

```csharp
foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
{
    if (!TestSupport.IsFed(vehicle, engine) || TestSupport.IsSolid(engine))
        continue;
    double rated = TestSupport.ComputePerformance(engine, ambientPressurePa: 0.0).MassFlowRateMax;
    double live = TestSupport.MassFlow(vehicle, engine);
    HarnessLog.Line($"{engine.Parent.Id}: {TestSupport.MaxThrust(engine) / 1000:F0}kN, Isp {TestSupport.VacuumIsp(engine):F0}s, flow {live:F2}/{rated:F2}kg/s");
}
```

## Gimbals

`TestSupport.ReadGimbalAngles(Vehicle, EngineController)` returns one `GimbalReading` per gimbal that moves a nozzle of the engine (several nozzles on one gimbal give one reading; no gimbal gives an empty list). Angles are radians about the gimbal's Y and Z axes, packed into a `float2` as (Y, Z):

- `Angles`, `AngleRates`: measured, from `GimbalControllerState`, which `Gimbal.UpdateModules` copies from the gimbal's `GimbalState` after each filter update.
- `Command`: the normalized command the flight computer wrote (`CommandY`, `CommandZ`), clamped to -1..1.
- `CommandAngles`: the angle target of the filter, `Command` times `MaxAngles`, limited to the unit circle when the gimbal has `ConstrainToCircle`, as `Gimbal.GetCommand` computes it.
- `MaxAngles`, `NaturalFrequency` (the gimbal's frequency limited by the controller's `NaturalFrequencyLimit`), `TotalThrust` (N of the nozzles on this gimbal in the last pass), and the `GimbalController` itself.

The actuator is a critically damped second-order filter with natural frequency w (`GimbalAxis.UpdateState`) whose slew rate is capped at `w * MaxAngle / (5 e)`. It is exact for any step size in the linear range, so a test can step it at a different dt than the solver and compare.

## Self-tests

| Test | Default | What it proves |
| --- | --- | --- |
| `rockets` | yes, skips without the `Test Vehicle 1` save | `MaxThrust`, `VacuumIsp` and `ComputePerformance` reproduce `VacuumData` and `SeaLevelData`; while the first stage burns in vacuum, `IsFed` agrees with `EngineHasPropellant`, `Throttle` reads 1, `MassFlow` equals the rated vacuum flow, the gimbal readings are in range and below the rate cap, and the burned mass over 5 s matches the live mass flow. |
| `gimbal-filter` | yes, pure | `GimbalAxis.UpdateState`: the small-step response follows the closed form `c (1 - (1 + w t) e^(-w t))` without overshoot, a full-range step peaks at the rate cap and settles, and one call at dt = 1 s matches 60 calls at 1/60 s (and a quarter second while rate limited). |
| `orbit-math` | yes, pure | Besides the circularization round-trip, orbits near e = 1 (1 - 1e-4 bound, 1 +- 1e-12 parabolic, 1 + 1e-4 hyperbolic): `GetNextTimeOfRadius`, `GetVelocityCciAtRadius`, the apsis times and `GetTimeSincePeriapsisThisOrbit` stay finite, the speed matches vis-viva, and the propagated state at the crossing time is on the radius. |

`orbit-math` logs a game defect without failing: on an `Orbit.Parabolic`, `GetNextTimeOfRadius` reports half the real time of flight, because `TimeOfTrueAnomaly` uses `Orbit.Parabolic.GetTimeFromPeTo(EccentricAnomaly)`, which has a factor 1/2 that `GetTimeFromPeTo(TrueAnomaly)` and the propagation do not. Re-check on each game build whether the line still appears.

## Maintenance on game update

- State lists read through `ModuleStateful<TModule, TState, TGlobalState, TFxState>.TryGetFrom(PartTree.States, ...)` and `StateList.GetState(module)`: `Rocket`/`RocketState`, `RocketCore`/`RocketCoreState`/`RocketCoreGlobalState`, `EngineController`/`EngineControllerState`/`EngineControllerGlobalState`, `ThrusterController`/`ThrusterControllerState`/`ThrusterControllerGlobalState`, `GimbalController`/`GimbalControllerState`. A retyped state breaks the build.
- `Rocket.UpdateRockets` is where the flags and rates come from: `EngineControllerState.IsPropellantAvailable` includes the active flag, `ThrusterControllerState.IsPropellantAvailable` does not, and `RocketCoreState.MassFlowRate` is zeroed and summed from the nozzle states each pass. Re-check these three assumptions.
- `RocketControllerData.ComputeFromRockets(ReadOnlySpan<Rocket>, float, float)` and the pressures `PartTree.RecomputeRocketControls` passes for `VacuumData` and `SeaLevelData` (0 and 101325 Pa).
- `SolidRocket` as the solid-motor type, `Constants.STANDARD_GRAVITY` for the specific impulse.
- The gimbal path `EngineController.Rockets` -> `Rocket.Nozzles` -> `Part.GimbalAsmb` -> `Gimbal.Controller`, and the copy of `GimbalControllerState.Angles`/`AngleRates` in `Gimbal.UpdateModules`.
- `TestSupport.CommandAngles` repeats the private `Gimbal.GetCommand` (clamp, circle constraint, scale by `GimbalAxis.MaxAngle`). A change there is invisible to the compiler and shows only as wrong `CommandAngles`.
- The `gimbal-filter` test writes the rate cap `w * MaxAngle / (5 e)` out on purpose, because `Gimbal.RATE_CAP_PER_NATURAL_FREQUENCY` and `Gimbal.DEFAULT_NATURAL_FREQUENCY` are constants compiled into the harness from the referenced build.
- The `Orbit.Parabolic` time-of-flight note above, and `Orbit.GetOrbitType`'s relative 1e-6 parabolic band that decides which orbit class each eccentricity of `orbit-math` gets.
