# Bundled test vehicles

Vehicle saves that ship with the harness, so a test can spawn the same vehicle on every machine.
The build copies this folder next to `HeadlessHarness.dll` (`vehicles/` in the deployed mod folder). `VehicleSpawner.SpawnFromSave`, `VehicleSpawner.ResolveVehicleSave` and `TestSupport.ResolveVehicleSaves` look a save name up here first, then in the `vehicles/` folder of each consumer mod, then in the game's `Vehicles` folder. Nothing is copied into the player's `Vehicles` folder.

Each save is the folder the game writes for a vehicle save: `meta.toml` (its `name` is the save name) and `vehicle.xml`.

| Save | What it exercises |
| --- | --- |
| `Test Vehicle 1` | The default flight-test vehicle. A crewed Mk1 command pod with a docking port, a cargo bay with six bay tanks, and a two-stage hydrolox core: an upper stage with a vacuum engine inside an interstage, and a lower stage with a sea-level engine and four radial boosters on radial decouplers, each with its own engine. Sequences: 1 ignites the core and booster engines, 2 drops the boosters, 3 drops the lower stage, 4 ignites the upper engine, 5 drops the pod. It covers liquid engines, radial and stack decouplers, several fuel stages and crew. |
| `Test Vehicle 2` | A crewed Mk1 command pod with a parachute bay, a cargo bay with six bay tanks, an upper stage with a vacuum engine inside an interstage, and a lower hydrolox stage with a sea-level engine, four landing legs and four solid boosters on radial decouplers. Sequences: 1 ignites the core engine and the solids, 2 and 3 drop the boosters pair by pair, 4 drops the lower stage, 5 ignites the upper engine, 6 drops the pod, 7 to 9 deploy and cut the two parachutes. It covers solid motors, landing legs, parachutes and a booster separation in two steps. |

Both saves were made on game build v2026.9.22.5482. A save that a later game build cannot read any more fails with the missing part ids; save it again in the game and replace it here.

Every `DecouplerData` record names its module with `InstanceOf="Decoupler"`. Since game build 2026.11.25.5949 the cargo bay has a second decoupler (`HeatshieldMount`), and `ModuleList.ApplySaveData` drops a record without `InstanceOf` on a part with two modules of one type, so the cargo bay decoupler would fire in sequence 0. A save written by that build or a later one has the attribute already.
