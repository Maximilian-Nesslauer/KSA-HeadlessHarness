# Save round trips

`SaveRoundTrip` writes game state through the game's own save code, reads it back through its own load code and compares what comes out. A field a module forgets to save, or reads back differently, shows up as the first difference between the two records, named by its element path. The player's save folder is never touched: everything stays in memory or goes next to the run log.

## Vehicle round trip

`SaveRoundTrip.Vehicle(vehicle)`:

1. serializes the vehicle (`Vehicle.SerializeSave`) and writes the record to XML the way the game writes a save,
2. reads the XML back (`VehicleData.OnDataLoad`),
3. builds a second vehicle from it the way a save load does (`Vehicle.CreateVehicleFromSaveGameData`, or `KittenEva.CreateKittenFromSaveData` for a kitten), registers it in the parent's child list and restores its target with `Vehicle.SetTarget`, as `CelestialSystem.DeserializeSave` does,
4. serializes that vehicle and compares the two records.

The second vehicle gets an id of its own (the celestial system registers astronomicals by id) and is compared under the source id. It is never stepped, its seats are cleared before it is destroyed (so the source's crew keep their missions), and it is gone when the call returns.

The result (`SaveRoundTrip.Result`) holds `Equal`, `FirstDifference` (an element path such as `/VehicleData/RootPartRef[0]/PartRef[1]/Components[0]/TankState[0]@Mass: '12.5' != '12.25'`), `Module` (the first part module whose save data differs, found with the game's `SaveDataEquality.AreEqual`, bit-exact for floating point), `FullTurnAngles` and both XML texts.

Two differences are part of how the game saves and are not counted:

- Orientations are saved as XYZ angles in radians (`TransformReference.RotationValue`, and `Body2Cce` and `Body2Phys` of the vehicle record, through `doubleQuat.ToXyzRadians`) and turned back into a quaternion on load, so an angle of pi can come back as -pi. An angle attribute of a `Rotation`, `Body2Cce` or `Body2Phys` element that differs by a full turn counts as equal; `FullTurnAngles` says how many there were.
- The bubble-frame `KinematicState` fields `PositionPhys`, `VelocityPhys`, `Body2Phys` and `BodyRates` are rebuilt from the saved inertial state during `Vehicle.DeserializeSave` (`PhysicsStates.UpdateFromAnalytic` and `SetBodyRates`). The save does not hold the bubble origin, and the inertial orientation is rounded, so these derived fields can differ after loading. They are left out; the authoritative `AnalyticState` position, velocity, orientation and body rates remain compared, as do the part and module states.

`SaveRoundTrip.FirstDifference(beforeXml, afterXml)` is public, so a test can compare any two save records with the same rules. `SaveRoundTrip.LogXmlPair` is internal.

The self-test `save-vehicle-roundtrip` (default) flies the `KSA_HEADLESS_VEHICLE` save for three seconds of burn and two of coast, sets its navigation target, then round-trips it.
It also checks that the source record is unchanged and no extra vehicle remains.
On a difference it writes both records next to the run log.

## Universe round trip

The game's universe save and load read three render objects that do not exist headless. The harness rewrites those call sites (see Maintenance below), so these run through the real game code:

| Member | What it does |
| --- | --- |
| `SaveRoundTrip.SerializeUniverse()` | The whole universe as the XML of a game save (`UniverseData.Create`, written with `GameSaves.UniverseSerializer` and `XmlHelper.SerializeWithoutNaN`, as `UniverseData.WriteTo` writes `universe.xml`). |
| `SaveRoundTrip.SerializeUniverse(data)` | Writes an existing `UniverseData` record with the same serializer. |
| `SaveRoundTrip.ReadUniverse(xml)` | Reads universe XML the way `UniverseData.LoadFrom` does. |
| `SaveRoundTrip.LoadUniverse(data)` | `Universe.DeserializeSave`, as the load menu runs it: every vehicle is destroyed and the saved ones are built again, the sim clock, kitten roster and camera are restored, the running part id is reset and the simulation speed is set to 1. |

After a load, the harness applies the run's rules again (a postfix on `Universe.DeserializeSave`), because the load replaces `Universe.Rules` with the rules of the save.
With `KSA_HEADLESS_RULES=settings`, the profile's start rules apply.
A `SimDriver` made before a load keeps its own clock, so create a new one with `HeadlessSession.CreateDriver` afterwards.

The game writes every number of a save rounded to four decimals (`NaNFilteringXmlWriter.WriteValue`), so a loaded orbit state is the saved one at that precision, not bit-identical to the live state before the save.

The same stock writer can round a positive crew dose below `0.00005 Sv` to `Sv="0"`.
`KittenRosterEntryData.LoadDose` reads zero, and `ShouldSerializeAcuteDoseData`, `ShouldSerializeLifetimeDoseData` and `ShouldSerializeAcuteDoseTimeData` then omit those elements on the next save.
This loses the original dose precision in the first write and changes the XML shape on the second write.
The universe test reports that difference and does not ignore dose data.

The self-test `save-universe-roundtrip` (opt-in) flies the `KSA_HEADLESS_VEHICLE` save, saves the universe, gives the loaded data the opposite rules and loads it.
Every vehicle must come back with the same save record, its orbit state exactly at save precision and a bit-identical mass.
A second save must write the same XML, and the requested rules must be in force.
With `KSA_HEADLESS_RULES=settings`, the profile's start rules are restored after a load, as after a system load.
It writes XML next to the run log, restores the rule values, simulation speed and controlled vehicle, and despawns what it spawned.
It is opt-in because loading destroys and rebuilds every vehicle and resets the running part id, which changes what later tests in the same run see.

```csharp
string xml = SaveRoundTrip.SerializeUniverse();
UniverseData data = SaveRoundTrip.ReadUniverse(xml);
SaveRoundTrip.LoadUniverse(data);
SimDriver driver = session.CreateDriver();
```

## Maintenance on game update

- The transpiler `HeadlessSession.SkipGroundClutterWithoutRenderer` on `CelestialSystem.SerializeSave`, `CelestialSystem.DeserializeSave` and `Universe.DeserializeSave`: it replaces the call pair `Program.GetPlanetRenderer()` then `PlanetRenderer.GroundClutterRenderer` with a static call that answers null without a `Program`, and aborts the bring-up unless it finds exactly one pair in each method. The Harmony prefix on `Program.GetPlanetRenderer` does not cover these sites, because the JIT inlines that one-line method into the rewritten methods and the inlined body dereferences the null `Program.Instance`.
- The transpiler `HeadlessSession.SkipProgramStatementsWithoutProgram` on `Universe.DeserializeSave`: it expects exactly three `Program.Instance` reads (`ParticleSystem.EmitterPool.ResetAll`, `ClearPlumeTrails`, `ClearExplosionVolumes`), each a straight call chain that ends in a call returning nothing, and wraps each in a null check. A changed count or a branch inside a chain aborts the bring-up.
- The postfix `HeadlessSession.ReapplyRulesAfterUniverseLoad` on `Universe.DeserializeSave(UniverseData)`, which sets `Universe.Rules` from `UniverseData.Rules`.
- `Universe.DeserializeSave` also calls `Program.MainViewport.BaseCamera.DeserializeSave` (the headless viewport answers), `ExplosionSoundSystem.StopAll`, `BurnContextMenu.Reset` and `Universe.ResetRunningId`. Re-check that no new render or UI call was added before or after them.
- `UniverseData.Create` reads `Program.MainViewport.BaseCamera` and `Program.GetMapController`, both of which the headless viewport answers.
- `GameSaves.UniverseSerializer`, `XmlHelper.SerializeWithoutNaN`, `XmlHelper.AttributeOverrides`, `UniverseData.OnDataLoad` and `VehicleData.OnDataLoad` with `Mod.Empty`.
- `Vehicle.CreateVehicleFromSaveGameData`, `KittenEva.CreateKittenFromSaveData`, `CelestialSystem.Get(string)`, `AstronomicalData.Get` and `AstronomicalData.IdHash` (XML-ignored, set by `OnDataLoad`).
- Records use the subtype dispatch and `SerializeLocalIdData` pass of `CelestialSystem.SerializeSave`, so EVA character data and control references are included. Control selection is restored with `Vehicle.SetControlPart`; ladder attachment uses the internal `KittenEva.RestoreLadderAttach(CelestialSystem, VehicleData, List<VehicleData>)` reflection key. Ladder attachment resolves on a later solver pass, so an immediate round-trip comparison can report that pending state as different.
- `CelestialSystem.DeserializeSave` restores navigation targets in a second pass with `Vehicle.SetTarget`; `Vehicle.DeserializeSave` alone does not restore them.
- `Universe.DestroyVehicle` with `CrewDisposition.EndMission` ends the missions of every kitten named in a seat (`Vehicle.EndAllCrewMissions`), which is why the vehicle round trip clears the copy's seats first (`IVASeat.AssignedKittenHash`).
- `SaveDataEquality.AreEqual` and `PartInstance.Components`, `Children` and `SubPartInstances` for naming the differing module.
- The two known differences: XYZ angles (`TransformReference`, `doubleQuat.ToXyzRadians`, `QuaternionEx.CreateFromXyzRadians`) and the rebuilt kinematic state (`Vehicle.UpdateAfterPartTreeModification` calling `PhysicsStates.UpdateFromAnalytic`). If the game starts to save the bubble origin or quaternions, narrow the comparison again.
- The save precision of `NaNFilteringXmlWriter` (four decimals, rounded away from zero), which the universe test reproduces.
