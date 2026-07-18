using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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

    private static FieldInfo? _manualInputsField;

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
