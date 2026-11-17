# Content checks, fingerprints and step statistics

The harness loads every enabled mod through the game's own content load. This page covers what a test can check about that content, how it identifies a vehicle state exactly, and how it reads the substep counts of the physics solver.

## Content load check

`ContentCheck` reads the content errors the game collected during the load: the read errors of every mod file, the errors of the full load and the errors of the streamed body textures. The game only prints them to its log and goes on, so a broken content mod loads with parts or systems missing and nothing fails.

| Member | What it does |
| --- | --- |
| `ContentCheck.LoadErrors()` | Every `ContentError` of the load, in the order the game recorded them. |
| `ContentCheck.ModErrors(modIds)` | The errors of the named mods, or of every mod except Core when `modIds` is null. An error the game could not attach to a mod (empty `Mod`) counts as not Core. |
| `ContentCheck.IsCore(error)` | True for an error of the stock content mod `Core`. |
| `ContentCheck.HeadlessOnly(errors)` | Shader failures caused by the missing renderer and their typed reference dependencies. Missing files and unrelated content errors remain failures. |
| `ContentCheck.Describe(error)` | One line in the format the game logs (`ContentErrorFormatter.Format`). |

The self-test `content-load` (default) fails on content errors outside Core, except shader failures caused by the missing renderer and references to those failed assets.
It lists each failure's kind, mod, file, tag and id.
Core errors and renderer-only failures are logged as information.
The dependency filter matches the asset type and id through `ModLibrary.TagTable`, because separate registries can reuse an id.
Every error is written to `<run>-content-load.csv`, including whether it is renderer-only.
The log names the loaded mods that supply parts.
`content-checks` (default) checks dependency classification, XML difference paths, full-turn angles and detection of changed orbit values and missing modules.

## Part checks

`PartCheck` builds every part template of a mod on its own, the way the editor builds a picked part (`new Part(id, template)`, then `Part.CreateOwnTree`), and checks it:

- the part builds without an exception,
- the static mass (`PartTree.StaticMassPropsAsmb`) is finite and positive,
- the part has colliders (`ColliderModule`),
- the aero mesh the game builds from those colliders is closed (`ClosedSurfaceChecks.IsClosed` on the solid panels, the check the game's own aero readout shows). A mesh of sheet panels only (a fin, a flat panel) has no hull to close and passes. Watertightness (`ClosedSurfaceChecks.IsWatertight`, no pinched edges) is reported, not required.

The aero mesh is built with `AerodynamicsBuilder.Start` and a null worker pool, which runs the build on the calling thread, so the result does not depend on thread timing. The part never joins a vehicle or the celestial system; its tree is disposed after the check.

| Member | What it does |
| --- | --- |
| `PartCheck.Templates(modIds)` | The part templates of the named mods, or of every mod except Core when null, sorted by id. Sub-part templates and templates marked `Test` are left out, as in `PartArchetypes.WarnOnMalformedParts`. |
| `PartCheck.Check(template, dumpDirectory)` | Checks one part and returns a `PartCheck.Result` (mass, colliders, panel counts, closed, watertight, pinched edges, the list of problems, the path of the mesh dump). `Passed` is true when there is no problem. |
| `PartCheck.CheckMods(modIds, dumpDirectory)` | `Check` for every template of the mods. |
| `PartCheck.ModsFromEnvironment()` | The list `KSA_HEADLESS_PART_MODS` names, or null. |
| `PartCheck.DumpDirectoryFromEnvironment()` | The dump folder next to the run log when `KSA_HEADLESS_PART_DUMP=1`, otherwise null. |

The self-test `content-parts` (opt-in) checks the parts of the mods `KSA_HEADLESS_PART_MODS` names, or of every mod except Core, writes one CSV row per part (`<run>-content-parts.csv`) and fails on any part with a problem. It skips when there is no part to check.

```csharp
foreach (PartCheck.Result r in PartCheck.CheckMods(new[] { "MyPartMod" }))
{
    if (!r.Passed)
        HarnessLog.Line($"[my-parts] FAIL {r.PartId}: {string.Join("; ", r.Problems)}");
}
```

## Vehicle fingerprint

`Fingerprint.Of(vehicle)` returns a `Fingerprint` record:

- `XmlSha256`: SHA-256 of the UTF-8 XML of `Vehicle.SerializeSave`, the record the game writes into a save (part tree, module states such as tank contents, engine and seat states, sequence state, flight computer, orbit and kinematic state).
- `StateBits`: the bit patterns of the orbit position and velocity (CCI), the attitude quaternion (`Body2Cce`), the body rates and the total mass.
- `Signature`: both, joined for one-line storage.

`Fingerprint.Of(vehicle, out saveXml)` also returns the XML that was hashed.
The fingerprint replaces occupied seats' kitten names with `Fingerprint.CrewPlaceholder`, because `KittenNameGenerator.Generate` uses `Random.Shared` across processes.
Seat occupancy remains in the fingerprint, but crew identity does not.
The live vehicle and its seats are not changed.

`Fingerprint.SaveXml(VehicleData)` writes a vehicle record the way the game writes a save (`XmlHelper.SerializeWithoutNaN` with `XmlHelper.AttributeOverrides`), and `Fingerprint.ReadSaveXml(xml)` reads it back and resolves its ids (`VehicleData.OnDataLoad`). The XML serializer for `VehicleData` is built once per process, on first use, which takes a moment.

The flight test adds the fingerprint of the final vehicle to its determinism signature. The baseline file is keyed by game build, star system, vehicle save, profile folder (a hash of `Constants.DocumentsFolderPath`) and the game rules in force, so runs in different profiles or with other rules never compare. `KSA_HEADLESS_STRICT_DETERMINISM=1` turns a DIFFER at the same spawn epoch into a test failure; without it a DIFFER is only logged. A different spawn epoch (a different set of tests ran before the flight test) is never a failure.

## Step statistics

`SimDriver` can record the substep counts of every physics bubble after each `Step`. They come from `PhysicsBubble.FrameStepDetail`, which the game resets at the start of every vehicle solver pass (`PhysicsBubble.Prepare`), so after `Step` it holds exactly that step. Only the deterministic counts are kept, never the tick timings.

| Member | What it does |
| --- | --- |
| `SimDriver.CollectStepStats` | Off by default. On, every `Step` appends one `StepStatsRow` per physics bubble. |
| `SimDriver.StepStats` | The rows: step index, sim time, `dt`, the bubble's first vehicle (`LeaderId`), its parent body, member count, segments, substeps, flight computer runs, smallest and largest substep, smallest and largest flight computer wake request, and the substeps per `PhysicsStepLimit` (`SubstepsByLimit`, indexed by the enum, names in `StepStatsRow.LimitNames`). |
| `SimDriver.SummarizeStepStats()` | The rows summed per bubble leader (`StepStatsTotal`, with `DominantLimit`). |
| `SimDriver.ResetStepStats()` | Drops the rows and starts the step index at 0 again. |
| `SimDriver.WriteStepStats(tag)` | Writes the rows to `<run>-<tag>.csv` next to the run log and returns the path. |

`MinStep` and `MinRequestedWake` are positive infinity (an empty CSV cell) when a bubble took no substep or the flight computer requested no wake. Every substep is attributed to exactly one limit, so the sum of `SubstepsByLimit` equals `Substeps`.

The self-test `step-stats` (default) flies the `KSA_HEADLESS_VEHICLE` save off rails for six steps with collection on and checks one row per step for its bubble, at least one substep per step, the per-limit sum, and no substep longer than the step; then it checks that `ResetStepStats` with collection off records nothing.

```csharp
SimDriver driver = session.CreateDriver();
driver.CollectStepStats = true;
driver.Step(1.0, 60);
foreach (StepStatsTotal t in driver.SummarizeStepStats())
    HarnessLog.Line($"{t.LeaderId}: {t.Substeps} substeps, mostly {t.DominantLimit}");
driver.WriteStepStats("my-step-stats");
```

## Environment variables

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_PART_MODS` | Comma-separated mod ids whose parts `content-parts` and `PartCheck.ModsFromEnvironment` check. Unset: every mod except Core. |
| `KSA_HEADLESS_PART_DUMP=1` | Writes the aero mesh of every checked part as a Wavefront `.obj` (`AeroDump.WriteMesh`) into `<run>-aero/` next to the run log. |
| `KSA_HEADLESS_STRICT_DETERMINISM=1` | A flight test determinism DIFFER at the same spawn epoch fails the test. |

## Maintenance on game update

- `ModLibrary.FullLoadErrors` and `ModLibrary.PreloadErrors`, internal static properties read by name (`ContentCheck.LoadErrors`), and the rule that the game prints the full load list and falls back to the preload list (`ModLibrary.ChooseErrorsToPrint`). `CelestialTextureStreamer.LoadStreamed` adds its errors through `ModLibrary.AddStreamErrors`. `ContentError` and `ContentErrors` are public; re-check that `ContentError.Mod` still holds the mod id.
- `ShaderReference.Compile` throws the renderer-only diagnostic after checking that its shader file exists. `ContentResolver.PropagateFailures` names each failed target as its CLR type, quoted id and `failed to load`; `ModLibrary.TagTable.TryGet` supplies the type for an XML tag. Re-check this diagnostic format and tag binding before changing the filter.
- `IVASeat.GetSaveData` creates a separate `IVASeat.SaveData` record with `AssignedKittenName`. The fingerprint masks that record, not the live seat.
- `ModLibrary.AllParts`, an internal static field read by name (`PartCheck.Templates`), a `SerializedCollection<PartTemplate>`; `PartTemplate.IsSubPart` and `SerializedId.Mod`.
- The part build: the `Part(string, PartTemplate, ...)` constructor and `Part.CreateOwnTree`; `PartTree.StaticMassPropsAsmb`; `ColliderModule` as the source of the aero shapes (`AeroMeshSource.Gather`).
- The aero build: `AerodynamicsBuilder.Start(source, previous, shell, pool)` runs `AeroMeshBuildJob.Execute` on the calling thread when the pool is null; `AerodynamicsBuilder.HasFailed` and `TryTakeFinished`; `PartTree.ExchangeAerodynamics` (so `PartTree.Dispose` clears the result); `AeroMesh.SolidPanels` and `SheetPanels`; `ClosedSurfaceChecks.IsClosed` and `IsWatertight`; `AeroDump.WriteMesh`. The aero code changed in 5901; re-check these names first.
- `Vehicle.SerializeSave` and `VehicleData`; `XmlHelper.SerializeWithoutNaN` and `XmlHelper.AttributeOverrides`, which list every module save data type. The harness builds its own `XmlSerializer` for `VehicleData`, because the game serializes it only inside `UniverseData`.
- `Vehicle._threadWorkerUpdateState` (already a reflection key of `VehicleSpawner`), read again by `SimDriver.Stats` to find each vehicle's bubble; `VehicleUpdateState.Bubble`, `PhysicsBubble.FrameStepDetail`, `PhysicsBubble.CollectVehicles`, `PhysicsBubble.NumVehicles` and `PhysicsBubble.Parent`; `PhysicsBubbleStepDetail` and `PhysicsStepLimit` (the CSV gets one column per limit, so a new limit adds a column). Re-check that `PhysicsBubble.Prepare` still resets the frame detail for every bubble at the start of each solver pass (`VehicleUpdateTask`), and that `NoteSubstep` is still the only place that counts a substep.
- The flight test's solid rocket rule: `EngineController.Rockets` and `SolidRocket`. A solid burns along its grain (`SolidRocket.UpdateState`, `SolidMotor.UpdateBurn`), while `EngineController.VacuumData` rates it at the default chamber pressure, so a burn phase with a solid firing is not measured.
- `Constants.DocumentsFolderPath` names the profile folder in the determinism key.
