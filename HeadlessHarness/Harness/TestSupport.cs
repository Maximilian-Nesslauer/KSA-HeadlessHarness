using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Helpers shared by the harness's own tests and consumer IHarnessTests: manual vehicle control,
// propellant checks, and spawn bookkeeping. Centralized so the drift-prone game couplings (the
// _manualControlInputs reflection key, the per-core propellant walk) live in exactly one place and
// are re-verified once per game update instead of in every consumer.
public static class TestSupport
{
    // Names a save in the game's Vehicles folder for tests that fly a real vehicle; the harness's
    // own flight test and consumer flight tests share it. Unset: those tests skip.
    public const string VehicleEnvVar = "KSA_HEADLESS_VEHICLE";

    // Plural counterpart for a test that runs several saves (a sweep, a cross-vehicle check):
    // ResolveVehicleSaves reads it. Deliberately separate from the singular VehicleEnvVar, which
    // run-headless.ps1 always exports for the flight test, so overriding the multi-vehicle set does
    // not collapse a consumer's list onto that one pin.
    public const string VehiclesEnvVar = "KSA_HEADLESS_VEHICLES";

    private static FieldInfo? _manualInputsField;

    // Resolves which Vehicles-folder saves a multi-vehicle test should run: the comma-separated
    // KSA_HEADLESS_VEHICLES if set, otherwise the given candidates. Only saves that actually exist
    // are returned (which saves a machine has is machine-specific), and a requested-but-missing save
    // is logged so a run that dropped one is not silent. The caller iterates the result and adds its
    // own per-save skips for saves that exist but are unsuitable (e.g. lacking the parts it needs).
    public static IReadOnlyList<string> ResolveVehicleSaves(params string[] candidates)
    {
        string? overrideList = Environment.GetEnvironmentVariable(VehiclesEnvVar);
        IEnumerable<string> requested = !string.IsNullOrWhiteSpace(overrideList)
            ? overrideList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : candidates;

        List<string> resolved = new();
        foreach (string id in requested)
        {
            if (VehicleSaveExists(id))
                resolved.Add(id);
            else
                HarnessLog.Line($"[harness] vehicle save '{id}' not found in {VehicleSaves.SaveFolderPath}; skipping it.");
        }
        return resolved;
    }

    private static bool VehicleSaveExists(string saveId)
    {
        foreach (VehicleSave save in VehicleSaves.AsSpan())
        {
            if (string.Equals(save.Id, saveId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // The game reads manual throttle/engine state from this private per-vehicle struct and exposes
    // no setter outside its input pipeline; writing the field directly is the headless equivalent
    // of the player's throttle input. Resolved lazily so a game rename of the field fails only the
    // tests that drive manual control, not every TestSupport caller through a
    // TypeInitializationException.
    public static void SetManualControlInputs(Vehicle vehicle, float throttle, bool engineOn)
    {
        _manualInputsField ??= AccessTools.Field(typeof(Vehicle), "_manualControlInputs")
            ?? throw new InvalidOperationException(
                "[HeadlessHarness] Vehicle._manualControlInputs not found - game version may have changed.");
        _manualInputsField.SetValue(vehicle, new ManualControlInputs { EngineThrottle = throttle, EngineOn = engineOn });
    }

    public static bool AnyActiveEngineFed(Vehicle vehicle)
    {
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            if (engine.IsActive && EngineHasPropellant(vehicle, engine))
                return true;
        }
        return false;
    }

    // An engine fires only if fed propellant; the game tracks that per rocket core (RocketCoreState),
    // read here through the vehicle's core state list the same way the in-game engine debug view does.
    public static bool EngineHasPropellant(Vehicle vehicle, EngineController engine)
    {
        var cores = vehicle.Parts.RocketCores.GetModulesAndStates(engine.Cores.AsSpan()).GetEnumerator();
        while (cores.MoveNext())
        {
            if (cores.Current.State.IsPropellantAvailable)
                return true;
        }
        return false;
    }

    // Snapshot the vehicle ids present before a test spawns anything; DespawnNewVehicles then tears
    // down everything that appeared since, including stages a staging split registered.
    public static HashSet<string> CollectVehicleIds(CelestialSystem system)
    {
        HashSet<string> ids = new();
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle v)
                ids.Add(v.Id);
        }
        return ids;
    }

    public static int CountVehicles(CelestialSystem system)
    {
        int count = 0;
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle)
                count++;
        }
        return count;
    }

    public static void DespawnNewVehicles(CelestialSystem system, HashSet<string> preexisting)
    {
        List<Vehicle> spawned = new();
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle v && !preexisting.Contains(v.Id))
                spawned.Add(v);
        }
        foreach (Vehicle v in spawned)
            VehicleSpawner.Despawn(v);
    }

    public static string Verdict(bool pass) => pass ? "PASS" : "FAIL";
}
