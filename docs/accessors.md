# Accessors

`TestSupport` gives typed access to the game privates that tests otherwise read with reflection of their own. Each member is bound by name on first use and throws `InvalidOperationException("[HeadlessHarness] <member> not found - game version may have changed.")` when the game renamed or retyped it, so a rename fails only the tests that use it, and every consumer shares one binding that is re-verified once per game update.

The solver writes the update state of a vehicle on worker threads during `SimDriver.Step`. Read it, and call the part tree members, between steps only.

## API

| Member | What it does |
| --- | --- |
| `ref ManualControlInputs GetManualControlInputs(Vehicle)` | A reference to `Vehicle._manualControlInputs`, the throttle, engine switch and thruster flags that `Vehicle.PrepareWorker` hands to the flight computer at the next step. Writing through it changes single fields; `SetManualControlInputs(vehicle, throttle, engineOn)` replaces the whole struct. |
| `VehicleUpdateState? GetUpdateState(Vehicle)` | `Vehicle._threadWorkerUpdateState`, the per-vehicle solver state. Null until the game first configures the vehicle (`Vehicle.UpdateVehicleConfiguration`), which every spawn path does. |
| `bool HadVehicleContact(Vehicle)` | `VehicleUpdateState.HadVehicleContactThisFrame`: true when the last step touched another vehicle. The solver clears it at the start of each step. |
| `bool HasStaticCollider(Vehicle)` | Whether `VehicleUpdateState.BepuHandles.StaticObjectCollider` holds a collider, the launch pad that `ConstraintSim.UpdateStaticObjectCollider` adds for a nearby landmark. |
| `PhysicsBubble? GetPhysicsBubble(Vehicle)` | `VehicleUpdateState.Bubble`, the bubble the vehicle is simulated in. The game exposes only the flag `Vehicle.HasPhysicsBubble`. |
| `void RecreateResourceManagers(PartTree)` | The internal `PartTree.RecreateResourceManagers`: rebuilds the resource managers of a tree whose parts or tanks a test changed by hand, as `EVADoor.GetBackPackPart` does. |
| `void FlushDirtyResourceManagers()` | The internal static `PartTree.FlushDirtyResourceManagers`: rebuilds the resource managers of every tree marked dirty. `SimDriver.Step` runs it before and after the solvers, so a test needs it only between its own changes and the next step. |

## Example

```csharp
Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "Probe", orbit);
ref ManualControlInputs inputs = ref TestSupport.GetManualControlInputs(vehicle);
inputs.EngineThrottle = 0.5f;
inputs.EngineOn = true;
driver.StepUntil(() => TestSupport.GetPhysicsBubble(vehicle) != null, 0.1, 10.0, "no bubble");
bool touched = TestSupport.HadVehicleContact(vehicle);
```

`examples/HarnessConsumerExample/ExampleSteppingTest.cs` is a complete test.

## Self-test

`accessors` (default) binds and reads every accessor on the flight-test vehicle (`KSA_HEADLESS_VEHICLE`) in orbit and skips when the run names no save or the save is missing.

## Maintenance on game update

Reflection keys, each bound lazily:

- `Vehicle._manualControlInputs` (type `ManualControlInputs`), through `AccessTools.FieldRefAccess`. Re-check that `Vehicle.PrepareWorker` still reads the manual inputs from it.
- `Vehicle._threadWorkerUpdateState` (type `VehicleUpdateState`), through `AccessTools.FieldRefAccess`.
- `BepuHandles.StaticObjectCollider`, a nullable `BepuPhysics.StaticHandle`, read by reflection because the harness has no reference to BepuPhysics.
- The internal instance method `PartTree.RecreateResourceManagers()` and the internal static `PartTree.FlushDirtyResourceManagers()`.

Game members read directly, which a game change breaks at compile time: `VehicleUpdateState.HadVehicleContactThisFrame`, `VehicleUpdateState.BepuHandles`, `VehicleUpdateState.Bubble`. Re-check their meaning: that the solver still clears the contact flag at the start of each step (`VehicleUpdateState.PrepareFromWorker`) and sets it for every vehicle contact, and that `ConstraintSim.UpdateStaticObjectCollider` still owns the static object collider.
