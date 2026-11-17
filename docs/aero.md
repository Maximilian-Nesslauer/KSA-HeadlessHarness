# Aerodynamics

`AeroProbe` measures the aerodynamic force and moment of a vehicle at any flow state you choose, without changing the vehicle or its later flight.
It runs the game's own surface load model, `LocalInclinationLaw.ComputeLoads` with a `SkinFrictionLaw`, on the vehicle's published aerodynamic mesh, with the same inputs `PhysicsStates.ComputeAerodynamicLoads` uses in flight, but on a private `AeroSurfaceFlow` in a private `BufferPool` that is freed before the call returns.

## Why not call the game's physics directly

`PhysicsStates.ComputeDerivatives` with `vehicle.Parts` gives the same loads, but it is not free of side effects. It takes the tree's live flow through `Aerodynamics.GetSurfaceFlow` (creating it if there is none) and calls `AeroSurfaceFlow.Update` on it. That flow caches its last flow direction and skips the recompute of running lengths and freestream shielding while the next direction is within one degree. A probe at another angle leaves its solution in the cache, and the solver then applies forces from the probe's direction in later steps: the measurement changes the flight it measures. `AeroProbe` never touches the live flow; the `aero-probe` test proves that.

## API

| Member | Does |
| --- | --- |
| `AeroProbe.Atmosphere(IParentBody body, double altitude)` | `AeroAtmosphere` at altitude (m above the mean radius): pressure, temperature, density from `PhysicalAtmosphere.ComputeConditionsAtAltitude`, speed of sound and gamma from the atmosphere's mixture, rounded to float the way the solver stores them. All 0 above `PhysicalAtmosphere.Height` or without an atmosphere (`HasAtmosphere` false). |
| `AeroProbe.Measure(Vehicle, IParentBody, altitude, speed, angleOfAttack, sideslip)` | Loads at an airspeed (m/s) and angles (rad) in the helm frame (`Vehicle.Helm2Body`: X forward, Y right, Z down). The vehicle moves through the air along `(cos a cos b, sin b, sin a cos b)`, so positive angle of attack is nose up. |
| `AeroProbe.MeasureAtMach(Vehicle, IParentBody, altitude, mach, angleOfAttack, sideslip)` | The same with a Mach number at that altitude. |
| `AeroProbe.Measure(Vehicle, IParentBody, altitude, double3 airVelocityBody, double3 bodyRates)` | The general form: the vehicle's velocity relative to the air (m/s) and its inertial body rates (rad/s), both in the body frame. |
| `AeroProbe.MeasureCurrent(Vehicle)` | Loads at the vehicle's own state: altitude above its orbit parent, `Vehicle.GetSurfaceVelocityCci` turned into the body frame, `Vehicle.BodyRates`. The game models no wind, so this is the flow the solver sees. |

`AeroLoads` holds:

- `ForceBody` (N) and `MomentBody` (N m about the center of mass), body frame (the vehicle assembly axes).
- `Drag`, `Lift`, `SideForce` (N) on wind axes built on the helm frame: drag along the flow, lift opposite helm +Z normal to the flow, side force toward helm +Y.
- `Speed`, `Mach`, `DynamicPressure`, `FlowDirectionBody` (the unit direction the air moves relative to the vehicle), the `Atmosphere` used, `MomentStiffnessBound`/`MomentDampingBound` per unit dynamic pressure, and the mesh's `PanelWidth` and `FaceCount`.

The loads are the raw surface loads. The solver also applies a drag saturation `1 / (1 + D dt / (m v))` and a torque limiter per step, and adds buoyancy; the probe leaves all three out. At the dynamic pressures of a normal ascent these are far below a percent.

Rules:

- Call it between `SimDriver` steps only. A mesh rebuild frees the surface the private flow reads, so the probe keeps nothing of the mesh after it returns.
- The vehicle needs a published mesh. `SimDriver` publishes it after each step, so step once after a spawn. Without a mesh (or after a failed build) the probe throws `InvalidOperationException`; the game then falls back to a plain drag-area model, which the probe does not reproduce.
- `LocalInclinationLaw` treats every Mach number below 1.3 as 1.3, so subsonic results are those of Mach 1.3.
- The surface math runs on `Vector<float>`, whose width depends on the CPU, so results are bit-identical on one machine only.

```csharp
session.CreateDriver().Step(1.0);   // publishes the aerodynamic mesh
HarnessData data = HarnessData.Create("aero-sweep", "aoa_deg,drag_n,lift_n,pitch_nm");
for (int aoa = 0; aoa <= 20; aoa += 5)
{
    AeroLoads loads = AeroProbe.MeasureAtMach(vehicle, home, 10_000.0, 2.0, aoa * Math.PI / 180.0, 0.0);
    data.AppendRow(aoa, loads.Drag, loads.Lift, loads.MomentBody.Y);
}
```

`examples/HarnessConsumerExample/ExampleAeroAndEngineTest.cs` is the complete worked example (opt-in, `example-aero-and-engine`).

## Self-tests

| Test | Default | What it proves |
| --- | --- | --- |
| `aero-probe` | yes, skips without the `Test Vehicle 1` save or an atmosphere on the home body | Along the stack axis lift and side force stay below 2 % of drag; lift at +-5 degrees is antisymmetric; drag grows with dynamic pressure over Mach 1.5, 3 and 6 with a drag area in a factor-2 band; loads are zero above the atmosphere; repeating the first measurement gives the same bits; the live flow of the tree (object, cached direction, all running length and exposure buffers) is identical before and after. |
| `aero-rebuild` | yes, skips without the save | Twice: spawn in vacuum, run the whole sequence list with the throttle closed, and after every step no tree is still rebuilding (`PartTree.IsRebuildingAerodynamics`) or failed (`HasAerodynamicsBuildFailed`); both runs end with the same vehicles and bit-identical `AeroMesh.PanelWidth`. |
| `aero-probe-vs-flight` | opt-in | Flies the save off-rails at 45 km and Mach 3.5 with the engines off and compares `Vehicle.AccelerationBody` after each 0.1 s step with the probe's force over mass, averaged over the step; must agree within 5 % (it agrees within 0.2 %). |

## Maintenance on game update

- The load path mirrored from `PhysicsStates.ComputeAerodynamicLoads`: the flow direction `-airVelocity / speed` in the body frame, `LocalInclinationLaw(float mach, float gamma)` with Mach from the float speed of sound and gamma from `AtmosphericMixture.ComputeGamma` at the float temperature, `SkinFrictionLaw(mixture, temperature, density, airspeed, null)`, `ComputeLoads(flow, friction, flowDirection, bodyRates / speed, centerOfMassAsmb)`, and the scaling by `0.5 * density * speed^2`. A change to any of these (a new input, another moment point, a wall temperature) is invisible to the compiler and shows in `aero-probe-vs-flight`.
- The atmosphere mirrored from `PhysicsEnvironment.RecomputePositionalValues`: altitude clamped at 0, no atmosphere at or above `PhysicalAtmosphere.Height`, and the float rounding of pressure, temperature, density and speed of sound.
- `AeroSurfaceFlow(AeroSurface, BufferPool)` and `AeroSurfaceFlow.Update(Vector3)`, the public `Aerodynamics.Mesh`, `AeroMesh.Surface`, `AeroMesh.PanelWidth` and `AeroSurface.FaceCount`, and `PartTree.AwaitAerodynamics`, `IsRebuildingAerodynamics`, `HasAerodynamicsBuildFailed`. The harness references `BepuUtilities.dll` for `BufferPool`.
- The one-degree direction cache in `AeroSurfaceFlow.Update` is the reason for the private flow. If the game drops it, the probe still works, but the explanation above is stale.
- Reflection keys, used only by the `aero-probe` test to read the live flow without creating one: `Aerodynamics._surfaceFlow` and `AeroSurfaceFlow._lastFlowDirection`. Missing keys fail that test with "game version may have changed".
- `Vehicle.Helm2Body` defines the angle-of-attack frame, with the body axes of `Vehicle.ComputeBody2Cce` (X forward, Y right, Z down). `Vehicle.AccelerationBody` is the non-gravitational acceleration the opt-in comparison reads.
