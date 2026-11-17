# Spawning, building and staging vehicles

`VehicleSpawner`, `VehicleBuilder` and `Eva` put test vehicles into the loaded system: from a vehicle save, from stock parts assembled in code, or as a copy of a live vehicle; in orbit or standing on the ground. They stage a vehicle the way the staging key does and take kittens out on EVA and back in.

Every spawn goes through the game's own `Vehicle.CreateVehicle` and registers the vehicle in its parent's child list, so the solvers pick it up on the next `SimDriver.Step`. Tear every vehicle down again with `VehicleSpawner.Despawn`, or snapshot the ids with `TestSupport.CollectVehicleIds` before the test and sweep with `TestSupport.DespawnNewVehicles` in a `finally`; the sweep also catches the stages a split made.

## Spawning

| Call | What it does |
| --- | --- |
| `SpawnFromSave(saveName, system, parent, id, orbit[, applyTankAssignments \| SpawnOptions])` | Resolves the save by name (see [Vehicle saves](#vehicle-saves)) and spawns it through `SpawnFromSaveData`. |
| `SpawnFromSaveData(VehicleSaveData, system, parent, id, orbit, SpawnOptions? = null)` | Builds a fresh part tree from the save and restores what `VehicleTemplate.CreateInto` restores: the active sequence, the per-sequence performance environments, the fuel links, and the mission. |
| `SpawnTree(PartTree, system, parent, id, orbit, SpawnOptions? = null)` | Spawns a part tree that belongs to no vehicle yet, such as one `VehicleBuilder.Build` or `PartTree.Deserialize` made. The vehicle takes the tree itself, so a tree spawns once. |
| `SpawnCopy(source, parent, id, orbit)` | Copies a live vehicle into a fresh tree, joins the source's physics bubble, and seats crew of its own. |
| `SpawnAtLocation(tree or saveName, body, latitudeDeg, longitudeDeg, headingDeg, id, heightAboveTerrain = null, SpawnOptions? = null)` | Stands the vehicle on the surface the way the launch menu places a launch (see [On the ground](#on-the-ground)). |
| `SpawnAtLaunchSite(saveName, body, locationId, id = null, SpawnOptions? = null)` and `SpawnAtLaunchSite(tree, body, locationId, id, ...)` | The same at a named location of the body, such as `"CCSFS LC-39A"` on Earth; a null id names the vehicle after the save. |
| `Despawn(vehicle)` | `Universe.DestroyVehicle` with `CrewDisposition.EndMission`. For a Kitten EVA it also ends the kitten's EVA assignment, so the kitten can crew a later spawn. |

`SpawnOptions` (a record, so `SpawnOptions.Default with { FillTanks = true }` works):

| Option | Default | Effect |
| --- | --- | --- |
| `SeatCrew` | `true` | Fills the free seats from the run's kitten roster (`SeatRandomCrew`). False leaves every seat empty; a seat that names a kitten of another roster is cleared either way, because such a seat counts as occupied but gives no control. |
| `FillTanks` | `false` | Runs `PartTree.ApplyTankAssignments`, as the launch menu does before a launch: a tank without contents fills with its automatic mix. |
| `Body2Cce`, `BodyRates` | identity, zero | Attitude and body rates of a spawn in orbit. A surface spawn takes both from the location. |
| `MakeIdUnique` | `false` | Picks `id`, `id_1`, `id_2`, ... instead of throwing when the id is taken. |

Helpers the spawn paths use and a consumer can call too: `EnsureIdAvailable(system, id)` throws when the id is registered (`LookupCollection.Register` only logs a duplicate and leaves the new vehicle out of the system, where nothing despawns it), `UniqueVehicleId(system, id)`, and `EnsurePartsLoaded(PartInstance, name)`, which names the missing parts of a save (`PartInstance.FindMissingParts`) and drops the connectors its parts no longer have (`PartInstance.DropStaleConnectors`), the checks `UncompressedVehicleSave.Load` makes.

When anything throws after `Vehicle.CreateVehicle` registered the vehicle, the spawn rolls back: a returned vehicle is despawned, a half-built one is deregistered and its part tree disposed. The exception still reaches the caller.

## Orbits

Built through `Orbit.CreateFromStateCci` from a state at periapsis. Altitudes are meters above the parent's mean radius, radii meters from its centre, angles radians, the plane angles as `Orbit` reports them (inclination from the parent's equator, the CCI XY plane).

- `CircularCci(parent, radius, time)`, `EllipticalCci(parent, periapsisRadius, apoapsisRadius, time)`
- `CircularAtAltitude(parent, altitude, time)`, `EllipticalAtAltitudes(parent, periapsisAltitude, apoapsisAltitude, time)`
- `InclinedEllipticalAt(parent, periapsisAltitude, apoapsisAltitude, inclination, longitudeOfAscendingNode, argumentOfPeriapsis, time)`
- `HyperbolicAt(parent, periapsisRadius, vInfinity, time, inclination = 0, lan = 0, argumentOfPeriapsis = 0)`
- `ApplyImpulse(orbit, time, dvCci)`: the orbit after an instantaneous velocity change in CCI.

## On the ground

`SpawnAtLocation` feeds the tree's mass properties and spawn bounds into `Vehicle.GetInitialKinematicStateForLocation`, as `VehicleLaunchMenu.LaunchVehicle` does. The vehicle stands with its body +X up, its bottom on the highest terrain under its footprint (on the deck of a launch pad when the point lies on one), and moves with the turning body. `headingDeg` turns it about the vertical: the compass direction of body +Z, clockwise from north (heading 0 is the game's orientation, body +Y east). `heightAboveTerrain` lifts it that many meters above the resting point, for a drop or a start above a collider. `ComputeSurfaceState` returns the orbit, attitude and body rates without spawning.

Headless the terrain modifiers of a body are not set up, because `Astronomical.SetupModifierRenderData` needs a renderer, so the terrain is the plain heightmap without a site's flattening. The pad height itself still counts.

A landing or splashdown raises `GroundImpactEvent` or `WaterSplashEvent`. Their particle handlers are skipped headless (see Maintenance).

## Vehicle saves

A save name resolves, in this order, against:

1. the `vehicles/` folder next to `HeadlessHarness.dll`, which ships `Test Vehicle 1` and `Test Vehicle 2` (see [`vehicles/README.md`](../vehicles/README.md));
2. the `vehicles/` folder next to the `mod.toml` of each consumer mod (a mod folder whose `mod.toml` names `HeadlessHarness`), in ordinal folder order;
3. the game's `Vehicles` folder of the profile.

The first save of a name wins; a shadowed one is logged at the first lookup. Every spawn logs where its save came from. Each bundled save is the folder the game writes for a vehicle save, `meta.toml` (its `name` is the save name) and `vehicle.xml`. The harness reads a bundled save with the game's own serializer and never registers it with the game's save lists or copies it into the player's folder.

- `FindVehicleSave(name)` returns a `VehicleSaveSource` (`Name`, `Origin`, `Folder`, `ModName`, `ReadData()`, `Describe()`) or null; `ResolveVehicleSave(name)` throws instead, naming every place it looked.
- `BundledVehicleSaves` lists the bundled saves in resolution order; `RefreshBundledVehicleSaves()` scans again.
- `TestSupport.ResolveVehicleSaves(candidates...)` returns the candidates that resolve anywhere, or the `KSA_HEADLESS_VEHICLES` list when that is set.

A consumer bundles its own saves by copying a save folder from the game's `Vehicles` folder into `vehicles/` of its project and deploying it next to its `mod.toml`:

```xml
<ItemGroup>
  <BundledVehicle Include="vehicles\**\*" />
</ItemGroup>
<Target Name="DeployBundledVehicles" AfterTargets="CopyToMods">
  <Copy SourceFiles="@(BundledVehicle)" DestinationFiles="@(BundledVehicle->'$(ModDeployDir)vehicles\%(RecursiveDir)%(Filename)%(Extension)')" SkipUnchangedFiles="true" />
</Target>
```

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_VEHICLE` | The save the flight test flies, resolved as above. |
| `KSA_HEADLESS_VEHICLES` | Overrides the candidate list of `TestSupport.ResolveVehicleSaves`, resolved as above. |

## Building vehicles from parts

`VehicleBuilder` assembles a part tree from part template ids the way the vehicle editor does:

```csharp
VehicleBuilder b = new VehicleBuilder(VehicleBuilder.StockParts.ProbeCoreSmall);
Part tank = b.AddStacked(b.Root, "_connector44", VehicleBuilder.StockParts.Tank3W3H, "_connector25");
Part engine = b.AddStacked(tank, "_connector39", VehicleBuilder.StockParts.VacuumEngine, "_connector5");
b.Sequence(engine, 1);
PartTree tree = b.Build(fillTanks: true);
Vehicle v = VehicleSpawner.SpawnTree(tree, system, home, "Probe", VehicleSpawner.CircularAtAltitude(home, 400_000, Universe.GetElapsedTime()));
```

- `new VehicleBuilder(rootPartId)` makes the root part in a tree of its own, with its attached internals (a command pod's cabin with the seats), as `VehicleEditor.SpawnPart` does.
- `AddStacked(parent, parentConnectorKey, partId, connectorKey)` joins a new part connector to connector with `Part.Connector.ConnectAndMerge`, which turns and moves it so the two connectors face each other.
- `AddSurfaceMounted(parent, partId, connectorKey, positionParentAsmb, normalParentAsmb, rollRad = 0)` places a radial part on the parent's surface, connects it to the surface (`Part.Connection.Connect`) and adds it as a child. The connector sits on the point and faces into the parent.
- `Sequence(part, n)` moves the part's sequenced modules into sequence `n`. A new part puts them in sequence 1, so a design names the sequence of every sequenced part.
- `Build(fillTanks = true)` numbers the resource groups from the decouplers and fills the tanks (`ResourceGroupList.CalculateStages`, the editor's automatic staging). An unspawned tree should be disposed.
- `FindPartTemplate(id)` and `FindConnector(part, key)` fail with the id, or with the part's real connector keys.

Connector keys are the `Key` attributes of a part's connectors in the part data. Canned designs, used by the self-tests:

| Design | Parts | Notes |
| --- | --- | --- |
| `TwoStageRocket(fillTanks = true)` | 8 | Crewed command pod, cargo bay, hydrolox upper stage (vacuum engine) in an interstage, lower stage (sea-level engine). Sequences: 1 lower engine, 2 interstage split, 3 upper engine, 4 pod split. |
| `ProbeCraft()` | 2 | Small probe core on an inline battery; no crew, no tanks. |
| `RcsCraft(fillTanks = true)` | 6 | Small probe core on a monopropellant tank with four RCS blocks around it. |

## Staging

`VehicleSpawner.Stage(driver, vehicle, settleSeconds = 0.5, dt = 0.1)` calls `SequenceList.ActivateNextSequence`, as the staging key does, steps `settleSeconds` (at least one step, because the activation only takes effect in the next step's input drain) and returns the vehicles that appeared, which is what a decoupler split registers. An empty list means the sequence ignited or toggled something, or nothing was left to activate.

## EVA

- `Eva.FindCrewedSeat(vehicle)`: the first seat with a kitten of the run's roster.
- `Eva.Request(vehicle, seat)`: `EVADoor.PerformEvaForSeat`; the kitten appears in the next step.
- `Eva.SpawnEva(driver, vehicle, seat, dt = 0.1)`: requests, steps once, and returns the `KittenEva`.
- `Eva.Board(kitten, host)`: `KittenEva.BoardVehicle` through the door `KittenEva.FindBoardTarget` finds within reach, or else the first door of the host with a free seat.

Both the EVA and the boarding hand the player's control to the new vehicle; the helpers restore `Program.ControlledVehicle`. With life support off (the harness default), `Vehicle.CheckEva` always allows the EVA and no suit supplies move.

## Self-tests

| Test | Default | What it checks |
| --- | --- | --- |
| `spawn-builder` | yes | Each canned design: part count, mass against the built tree, crew, sequence layout, control after one step; the rocket stages twice; the RCS blocks sit on the tank surface. |
| `spawn-orbits` | yes | Each orbit builder against the elements `Orbit` reports. |
| `spawn-bundled` | yes | The bundled saves resolve to the harness bundle and spawn crewed; the consumer bundle scan. |
| `spawn` | yes | `SpawnCopy` keeps the orbit and gives the copy crew of its own. |
| `spawn-surface` | opt-in | Landed spawns on the Earth pad, on open ground with a heading, and on the Moon stay put for 30 s. |
| `eva` | opt-in | A kitten goes out, boards again, goes out again and is despawned. |

## Maintenance on game update

Patch targets (`HeadlessSession.InstallSpawnPatches`):

- `Celestial.TrySpawnGroundImpact` and `Celestial.TrySpawnWaterSplash`, skipped: from 50 J of impact energy they start particles through `Program.Instance.ParticleSystem`, which is null headless. Re-check that both are still presentation only.

Game members and assumptions:

- `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, Part root, orbit)` registers the vehicle in the system inside the constructor and takes `root.Tree` as its part tree; `LookupCollection.Register` only logs a duplicate id. `CelestialSystem.All.Get` and `Deregister` back `EnsureIdAvailable` and the rollback.
- The save restore of `VehicleTemplate.CreateInto`: `SequenceList.SetActiveSequence` after `PartTree.EnsureDerived(DerivedData.Sequences)` (because `SequenceList.Add` advances the active sequence for every sequence it adds at or below it), `SequenceList.ApplyEnvironments`, `FuelLinkList.ApplySaveData`, `PartTree.Mission`.
- The save checks of `UncompressedVehicleSave.Load`: `PartInstance.FindMissingParts` and `PartInstance.DropStaleConnectors`.
- Reading a save folder: `SaveMetaData.FromDirectory` (the `meta.toml` name), `VehicleSaveData.ExistsIn` and the steps of `VehicleSaveData.LoadFrom` (`VehicleSaves.VehicleSerializer`, then `OnDataLoad(Mod.Empty)`), the file name `vehicle.xml`. `UncompressedVehicleSave.Directory` gives the folder of a game save. Re-check that the save format still has these two files.
- `ModLibrary.LocalModsFolderPath` for the consumer scan, which marks a consumer the same way `HarnessRunner` does (its `mod.toml` names the harness assembly).
- Surface placement: `Vehicle.GetInitialKinematicStateForLocation`, `PartTree.ComputeInertMassPropertiesAsmb`, `ComputePropellantMassPropertiesAsmb` and `ComputeSpawnBoundingBoxAsmb`, as `VehicleLaunchMenu.LaunchVehicle` calls them; its body frame (+X up, +Y east, +Z north, from `Vehicle.ComputeBody2Cce`), which the heading turn relies on; `IParentBody.GetAngularVelocityCci` along CCI +Z; `Celestial.GetLocation` and `LocationReference.Latitude` / `Longitude`. `Astronomical.SetupModifierRenderData` is never called headless, because it needs a renderer.
- The builder follows `VehicleEditor.SpawnPart` and `Attach`: `new Part(id, template)`, `Part.CreateOwnTree`, `PartTree.ReinitializeDerivedValues`, the `AttachedInternal` children joined with `Part.AddChild` and `Part.Connection.Connect(part, part)`, `Part.Connector.ConnectAndMerge`, `Part.SetAllSubtreeModulesSequence`, `ResourceGroupList.CalculateStages(reconfigureTankContents)` as in `VehicleEditor.FlushDeferredStaging`, and `ModLibrary.Get<PartTemplate>`, which throws `NullReferenceException` for a missing id. The surface mount repeats the pose change of `ConnectAndMerge`; re-check it when that method changes.
- The canned designs name stock part ids and connector keys (`VehicleBuilder.StockParts`); `spawn-builder` fails with the missing id or key when stock renames one.
- `VehicleBuilder.Rotate` repeats `Double3Ex.Transform(double3, doubleQuat)`, because calling that extension needs a reference to BepuUtilities.
- Staging: `SequenceList.ActivateNextSequence` and `GetNextSequenceNumber`, and the split registering a new vehicle in the system.
- EVA: `EVADoor.PerformEvaForSeat` queues on `InputEvents.EvaSpawnBuffer`, which `InputEvents.ApplyInputEvents` drains; `EVADoor.CreateKittenEva` names the kitten after its roster entry and assigns the entry to the vehicle id `"EVA"` (`Eva.EvaVehicleId`), which `Despawn` relies on to end the EVA kitten's mission; `KittenEva.FindBoardTarget`, `CheckBoarding` and `BoardVehicle`; `Vehicle.CheckEva`. Both `EVADoor.SpawnEva` and `KittenEva.BoardVehicle` set `Program.ControlledVehicle`.
