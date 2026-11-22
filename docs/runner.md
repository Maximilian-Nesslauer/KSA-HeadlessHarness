# Runner, run script and results

How a harness run is driven: the run script, the test loop with its watchdog and cleanup, the result files, test parameters, the mod lifecycle hooks, and the log folder.

## The test loop

`HarnessRunner.RunDiscovered` loads the consumer assemblies, discovers every `IHarnessTest`, applies the filter and the opt-in rule, and runs the tests ordered by `Name`. For each test it:

1. snapshots the vehicle ids of the loaded system,
2. logs `[harness] start <name> (sim epoch <seconds> s, <budget>)`,
3. arms the watchdog with the test's budget and calls `Run`,
4. cleans up (see below),
5. logs `[harness] <name>: PASS|FAIL|SKIP|INFRA FAIL in <n>ms wall, <s>s sim` and closes the test's entry in the results.

A test that returns 0 but recorded a failed check through `HarnessChecks` counts as a failure. A test that returns 0, recorded no check and reported a skip counts as skipped. A test that returns 2 (`HarnessExit.InfrastructureFailure`, what `HarnessChecks.Finish` returns after `Infra`) counts as an infrastructure failure: it found the environment unfit to test what it tests, so the run exits with 2. Every other non-zero return is a failure.

### Cleanup after each test

What a test leaves behind is put back before the next test starts:

- every vehicle that was not there before the test is despawned through `VehicleSpawner.Despawn`, each with a `[harness] LEAK: test '<name>' left vehicle '<id>' behind` line and an entry in the test's `leakedVehicles`,
- an active auto warp is stopped (`Universe.AutoWarpStop`) and the simulation speed is set back to its value before the first test (`Universe.SetSimulationSpeed(speed, alert: false)`); a loss of control sets it to 1,
- the run's game rules (`Universe.Rules`) are restored, also when a save load replaced the rules object,
- the terrain modifier data is put back as the run has it (see [Spawning](spawn.md#terrain)): a body that held it at the system load and lost it gets it again, and in a bare terrain run a body a test gave it loses it again; a body that is still not as the run setting has it after that is an infrastructure failure (the same check runs once before the first test),
- `PhysicsBubble._forceOffRails` is turned off,
- the kFS switch `KfsPrototype.Enabled` is set back to its value before the first test,
- the alert list is cleared (`TimedAlert.ClearAll`), which only the game UI would drain.

Each restore that was needed is logged. A cleanup step that throws is an infrastructure failure, because the tests after it no longer start clean. Mods still loaded through `HarnessMods` are listed after each test and unloaded at the end of the run.

### Watchdog

`IHarnessTest.TimeoutSeconds` (default `null`) is the wall-clock budget of one `Run` in seconds. Null takes the run default from `KSA_HEADLESS_TEST_TIMEOUT`; zero or less means no budget for that test. `KSA_HEADLESS_RUN_TIMEOUT` bounds the whole run, counted from the harness start; the run script sets it 30 s below its own kill timeout.

When a budget runs out, a background thread logs `[harness] WATCHDOG: '<phase>' is still running after its budget ...`, marks the running test as `timeout` in the results, writes the result files and ends the process with exit code `3`. The phase names the test, its cleanup, the bring-up or the session validation, so a hang outside a test is named too.

```csharp
public sealed class LongFlightTest : IHarnessTest
{
    public string Name => "long-flight";
    public int? TimeoutSeconds => 600;
    public int Run(HeadlessSession session) { /* ... */ return 0; }
}
```

### Culture

`HarnessMain.Run` sets the invariant culture for the whole process (`CultureInfo.DefaultThreadCurrentCulture`, `CurrentCulture` and the UI variants) before anything logs, so every log line, CSV and report reads `4.895E+009` and `1.5` on any machine.

## Result files

Every run writes two files next to its run log, named after it:

- `<run>-results.json`: the run and every test, for tools.
- `<run>-junit.xml`: the same as JUnit XML, for CI test reports. Infrastructure failures and timeouts are `<error>`, failures `<failure>`, skips `<skipped>`. A run that failed outside any test gets an extra test case `harness`.

The JSON (schema 1):

| Field | Meaning |
| --- | --- |
| `runId`, `log` | the run log basename and path |
| `exitCode`, `outcome` | the process exit code and `pass`, `fail`, `infra` or `timeout` |
| `note` | why the run failed outside the tests, or the watchdog message |
| `wallMs` | wall time from the harness start |
| `gameVersion`, `harnessVersion` | `VersionInfo.Current.VersionString` and the harness assembly version |
| `system` | the loaded system id |
| `profilePath`, `instancePath` | the game profile folder (`Constants.DocumentsFolderPath`) and `STARMAP_INSTANCE_PATH` |
| `rules` | `request` (`KSA_HEADLESS_RULES`), `radiation`, `lifeSupport` after the bring-up |
| `terrainModifiers` | true when the bodies have their terrain modifiers, false with `KSA_HEADLESS_BARE_TERRAIN=1` |
| `seed`, `params`, `testFilter` | `KSA_HEADLESS_SEED`, `KSA_HEADLESS_PARAMS`, `KSA_HEADLESS_TESTS` |
| `totals` | test counts per outcome, plus `runInfrastructureFailures` outside any test |
| `optInSkipped` | the opt-in tests a default run left out |
| `tests[]` | `name`, `suite` (assembly), `outcome` (`pass`, `fail`, `skip`, `infra`, `timeout`), `wallMs`, `simSeconds`, `startEpochSeconds`, `startEpochNanoseconds`, `checks` (`total`, `failed`, `skipped` from `HarnessChecks`), `logSkips` (lines that start with `[<name>] SKIP`), `logChecks` (lines in the check format `[<name>] TEST ... => PASS|FAIL` not written by `HarnessChecks`), `leakedVehicles`, `detail` |

The files are written at the end of the run, and by the watchdog when it ends a hung run. A run killed from outside (exit 3 from the script) has no result files.

## Test parameters

A test reads its tunables with `TestSupport.Param`, so it needs no environment variable of its own:

```csharp
double dt = TestSupport.Param("my-test.dt", 0.1);
int samples = TestSupport.Param("my-test.samples", 16);
bool verbose = TestSupport.Param("my-test.verbose", false);
string save = TestSupport.Param("my-test.save", "Test Vehicle 1");
```

The values come from `KSA_HEADLESS_PARAMS` (`run-headless.ps1 -Param "my-test.dt=0.05;my-test.samples=32"`). Names compare without case, the last entry of a name wins, a value may hold `=` but not `;`, numbers parse with the invariant culture, and booleans accept `true/false`, `1/0`, `yes/no`, `on/off`. Each value a test reads is logged once per test with its source. A malformed list (an entry without `=`) fails the run at start; a value of the wrong type throws `InvalidOperationException` in the test. At the end of the run the harness warns about names no test read. `TestSupport.HasParam(name)` tells whether a name is set. Prefix the names with the test name, because the list is shared by the run.

## Seed

`KSA_HEADLESS_SEED` (`-Seed`) is an integer shared by consumer tests and the avionics upset source.
`HarnessRun.Seed` reads it, or returns null when unset or set to `off`.
Unset uses avionics seed 0; `off` retains the game's unseeded source.
Other values fail the run at start.
`HarnessRun.CurrentTest` is the name of the running test.

## Mod lifecycle hooks

StarMap applies the patches that call `[StarMapImmediateLoad]` and `[StarMapAllModsLoaded]` only after every `[StarMapBeforeMain]` method has run, and the harness runs the whole suite inside its own, so a mod under test never has its hooks called and its Harmony patches never applied. `HarnessMods` calls them:

```csharp
HarnessMods.InvokeAllModsLoaded("MyMod");
try
{
    // the mod's patches are live here
}
finally
{
    HarnessMods.Unload("MyMod");
}
```

`InvokeAllModsLoaded(modId)` finds `mod.toml` in the game's `Content/<id>` or the profile's `mods/<id>` folder (the order StarMap uses), reads the `[StarMap]` `EntryAssembly` (default: the mod id), resolves that assembly through the harness load context (the copy a consumer test binds to), finds the class marked `[StarMapMod]`, creates one instance, and calls its `[StarMapImmediateLoad]` methods with the mod's `KSA.Mod` (`ModLibrary.Find`, or `Mod.MakeUsing` on its `mod.toml` for a mod that is not in the manifest), then its `[StarMapAllModsLoaded]` methods. `Unload(modId)` calls its `[StarMapUnload]` methods. Attributes match by type name and signature, as StarMap matches them.

The calls are reference counted per mod id: only the first load and the unload that balances the last load reach the hooks, so nested loads in several tests work, and an unload without a load does nothing. If a load hook throws, the unload hooks run to take back what was applied, and the exception reaches the test. `IsLoaded(modId)` tells the state. The runner unloads every mod still loaded at the end of the run. `ExampleModLifecycleTest` in the example consumer is the worked pattern.

A mod that is also in the manifest (`-PreMods`, `-ContentMods`, `-KeepManifest`) was already created by StarMap, which ran its `[StarMapBeforeMain]`; `HarnessMods` creates a second instance of the mod class for the hooks.

## Log folder

At the start of each run, inside the run mutex, the harness prunes its log folder (`%TEMP%\ksa-headless-harness`, and the folder of `KSA_HEADLESS_LOG` when that differs): it keeps the newest 200 run logs (`run-*.log`) and every run log younger than 2 days, and deletes an older run log together with its side files (`<run>-*` and `<run>.*`: results, JUnit, data and report files, the archived game log). Other files, such as the determinism signatures, are never touched. The pruning is logged.

## Run script

`scripts/run-headless.ps1` parameters added to the existing ones:

| Parameter | Effect |
| --- | --- |
| `-ContentMods a,b` | enable these mods after Core and HeadlessHarness in the minimal manifest; each must be a folder in the profile's `mods` folder |
| `-PreMods a,b` | enable these mods after Core and before HeadlessHarness, for a mod whose `[StarMapBeforeMain]` must patch the content load first |
| `-KeepManifest` | run with the profile's own manifest (a dedicated instance); not with `-ContentMods` or `-PreMods` |
| `-Param "a=1;b=2"` | `KSA_HEADLESS_PARAMS` |
| `-EnvVars "NAME=value;..."` | extra environment variables for the game process, put back afterwards |
| `-Seed n` | `KSA_HEADLESS_SEED` |
| `-TestTimeoutSec n` | `KSA_HEADLESS_TEST_TIMEOUT` (0, the default: no default budget) |
| `-Deploy "src=name;..."` | copy mod folders into the profile's `mods` folder for this run; an existing target fails the run |
| `-SaveSource a;b -SaveName x;y` | copy vehicle saves into the profile's `Vehicles` folder for this run; a save of that name is moved aside to `Vehicles.harnessbak` and put back; the game names a save by the `name` in its `meta.toml`, not by its folder, so keep the two the same |
| `-TimeoutSec n` | kill timeout, default 900; the harness gets `KSA_HEADLESS_RUN_TIMEOUT` = n - 30 when n >= 60 |

The script clears every variable it sets before and after the run, prints the result totals, and lists the results file, the JUnit file and every other file of the run.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\run-headless.ps1 -InstancePath "D:\KSA\Instances\Dev" `
    -ContentMods MyParts -Tests my-flight -Param "my-flight.dt=0.05" -Seed 7 -TestTimeoutSec 300
```

## Environment variables

| Variable | Effect |
| --- | --- |
| `KSA_HEADLESS_TEST_TIMEOUT` | default per-test budget in seconds; unset or 0: none |
| `KSA_HEADLESS_RUN_TIMEOUT` | budget of the whole run in seconds; unset or 0: none |
| `KSA_HEADLESS_PARAMS` | test parameters `name=value;...` |
| `KSA_HEADLESS_SEED` | integer consumer and avionics seed, or `off` for the game's unseeded source |

## Exit codes

`3` is now also the harness's own code: the watchdog ended a test or the run that outlived its budget. The run script uses the same code when it kills a run that did not end itself.

## Maintenance on game update

The runner adds no Harmony patch and no reflection key. It reads and writes these public game members, all compile-time bound, so a rename breaks the build:

- `Universe.SimulationSpeed`, `Universe.SetSimulationSpeed(double, bool)`, `Universe.IsAutoWarpActive`, `Universe.AutoWarpStop(bool)`: the speed restore. Assumes that `SetSimulationSpeed` with `alert: false` only sets the field.
- `TimedAlert.ClearAll`: assumes the alert list is the only state it clears.
- `PhysicsBubble._forceOffRails` (public static field).
- `KSA.Kfs.KfsPrototype.Enabled`: assumes the setter only counts the off switches besides storing the value.
- `Universe.Rules` (`GameRules.Radiation`, `GameRules.LifeSupport`): `HeadlessSession.CaptureRunRules` and `RestoreRunRules`. Re-check that `GameRules` still holds only these two rules.
- `Universe.GetElapsedTime`, `UniverseTime.Seconds`, `UniverseTime.Nanoseconds`: the start epoch of each test.
- `VersionInfo.Current.VersionString`, `Constants.DocumentsFolderPath`, `Universe.CurrentSystem`: the results file.
- `ModLibrary.LocalModsFolderPath`, `ModLibrary.Find(string)`, `Mod.MakeUsing(string, string)`: `HarnessMods`.

StarMap assumptions in `HarnessMods`, re-checked against the StarMap source on each StarMap update: the hook attribute names (`StarMapModAttribute`, `StarMapImmediateLoadAttribute`, `StarMapAllModsLoadedAttribute`, `StarMapUnloadAttribute`) and their signatures (`IsValidSignature`), the mod folder order in `RuntimeMod.TryCreateMod` (`Content/<id>` before the profile's `mods/<id>`), the `[StarMap]` `EntryAssembly` key, and that StarMap patches `ModLibrary.LoadAll` and `Mod.PrepareSystems` only after the `[StarMapBeforeMain]` methods ran (`StarMapCore.Init`). In the 2026.11.16.5901 build `Mod.PrepareSystems` no longer exists, so StarMap calls no `[StarMapImmediateLoad]` method in a normal game either; `HarnessMods` still calls them, as the StarMap contract describes.
