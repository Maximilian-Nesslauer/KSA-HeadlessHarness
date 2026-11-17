using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Vehicle control, game rules, probe power, avionics and missions. Vehicle.UpdateControl decides
// after every solver apply whether a vehicle is controllable (Vehicle.ComputeControl over the pod
// crew and the probe core power), and on the step a vehicle loses control,
// Vehicle.ReleaseFlightComputer queues its Auto attitude and burn modes back to Manual. These
// helpers read and steer that state without a player.
public static partial class TestSupport
{
    // Seeds the avionics upsets that the radiation rule rolls (Vehicle.RollUpset). An integer is
    // the seed; "off" keeps the game's unseeded Random. Unset or blank seeds with 0, so a run with
    // radiation on repeats.
    public const string SeedEnvVar = "KSA_HEADLESS_SEED";

    private static FieldInfo? _controlField;
    private static FieldInfo? _upsetRandomField;
    private static MethodInfo? _releaseFlightComputer;

    #region Control

    // The game's own verdict from the last Vehicle.UpdateControl, which runs in every
    // Universe.ApplyVehicleSolvers. A vehicle that has not been stepped yet reads Controllable, the
    // default of the field. ForceControllable does not change this value, only the answer of
    // Vehicle.IsControllable.
    public static VehicleControl GetControl(Vehicle vehicle)
    {
        _controlField ??= BindField("_control", typeof(VehicleControl));
        return (VehicleControl)_controlField.GetValue(vehicle)!;
    }

    public static bool IsControllable(Vehicle vehicle) => vehicle.IsControllable;

    // Seats crew in every free seat (VehicleSpawner.SeatRandomCrew), refills every battery, and
    // predicts what Vehicle.ComputeControl will answer after the next step. It does not step.
    public static ControlExpectation EnsureControllable(Vehicle vehicle)
    {
        int seated = vehicle is not KittenEva && HasSeatToFill(vehicle) ? VehicleSpawner.SeatRandomCrew(vehicle) : 0;
        int refilled = RefillBatteries(vehicle);
        return PredictControl(vehicle) with { CrewSeated = seated, BatteriesRefilled = refilled };
    }

    // A seat that is empty or names a kitten not crewing this vehicle, the seats SeatRandomCrew fills.
    private static bool HasSeatToFill(Vehicle vehicle)
    {
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash == KeyHash.Zero)
                return true;
            KittenRosterEntryData? kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash);
            if (kitten == null || !string.Equals(kitten.AssignedVehicleId, vehicle.Id, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Mirrors Vehicle.ScanControls without its side effects: a control module in a habitat zone is
    // a command pod, which counts as able when a kitten of this run's roster sits in that zone and
    // is not KIA (nor incapacitated while life support is on). Every other control module is a
    // probe core, powered unless its avionics failed or reboot (radiation rule only), its power
    // switch is off or its circuit has no charge. A storm standby is not predicted, because it
    // depends on the space weather at the next step.
    public static ControlExpectation PredictControl(Vehicle vehicle)
    {
        if (vehicle is KittenEva)
        {
            bool eva = vehicle.IsControllable;
            return new ControlExpectation(eva, eva ? VehicleControl.Controllable : VehicleControl.CrewIncapacitated,
                CommandPodCrew.NoCommandPod, ProbeCorePower.NoProbeCore, 0, 0, "kitten EVA");
        }

        PartTree tree = vehicle.Parts;
        if (tree.Controls.NumModules == 0)
            return new ControlExpectation(false, VehicleControl.NoCrew, CommandPodCrew.NoCommandPod,
                ProbeCorePower.NoProbeCore, 0, 0, "no control module");

        tree.EnsureHabitatZones();
        UniverseTime now = Universe.GetElapsedTime();
        double nowSeconds = now.Seconds();
        CommandPodCrew pod = CommandPodCrew.NoCommandPod;
        ProbeCorePower probe = ProbeCorePower.NoProbeCore;
        foreach (Control control in tree.Modules.Get<Control>())
        {
            HabitatZone? zone = HabitatZone.FindZone(tree, control.Parent);
            if (zone != null)
            {
                CommandPodCrew crew = ZoneCrew(vehicle, zone, now);
                if ((int)crew > (int)pod)
                    pod = crew;
                continue;
            }
            ProbeCorePower core = PredictProbeCore(tree, control.Parent, nowSeconds);
            // The game keeps the highest value (Vehicle.Max), and Powered is the highest.
            if ((int)core > (int)probe)
                probe = core;
        }

        VehicleControl predicted = Vehicle.ComputeControl(pod, probe);
        return new ControlExpectation(predicted == VehicleControl.Controllable, predicted, pod, probe, 0, 0,
            $"pod {pod}, probe core {probe}");
    }

    private static CommandPodCrew ZoneCrew(Vehicle vehicle, HabitatZone zone, UniverseTime now)
    {
        bool crewed = false;
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash == KeyHash.Zero || HabitatZone.FindZone(vehicle.Parts, seat.Parent) != zone)
                continue;
            KittenRosterEntryData? kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash);
            if (kitten == null || kitten.Kia)
                continue;
            if (!Universe.Rules.LifeSupport || !kitten.Condition.IsIncapacitated(now))
                return CommandPodCrew.Able;
            crewed = true;
        }
        return crewed ? CommandPodCrew.Incapacitated : CommandPodCrew.Empty;
    }

    private static ProbeCorePower PredictProbeCore(PartTree tree, Part part, double nowSeconds)
    {
        Avionics? avionics = FirstOrNull(part.Modules.Get<Avionics>());
        if (Universe.Rules.Radiation && avionics != null)
        {
            if (avionics.ComputeHealth() == AvionicsHealth.Failed)
                return ProbeCorePower.Failed;
        }
        PowerConsumer? consumer = FindProbeConsumer(part);
        if (consumer?.Mode == PowerSwitchMode.Off)
            return ProbeCorePower.SwitchedOff;
        if (consumer != null && !consumer.HasPower(tree))
            return ProbeCorePower.Unpowered;
        if (Universe.Rules.Radiation && avionics != null && avionics.IsRebooting(nowSeconds))
            return ProbeCorePower.Rebooting;
        return ProbeCorePower.Powered;
    }

    #endregion

    #region Force controllable

    // Vehicles the Vehicle.IsControllable postfix answers true for, with the number of open scopes
    // that hold each. Read on the main thread only: every game reader of IsControllable runs there
    // (Vehicle.UpdateControl in Universe.ApplyVehicleSolvers, the input drain and the UI).
    private static readonly Dictionary<Vehicle, int> _forced = new(ReferenceEqualityComparer.Instance);

    internal static bool IsForcedControllable(Vehicle vehicle) =>
        _forced.Count > 0 && _forced.ContainsKey(vehicle);

    // Makes Vehicle.IsControllable answer true for these vehicles until the scope is disposed,
    // without changing the game's own verdict (GetControl) or any other vehicle. Because
    // Vehicle.UpdateControl reads the same getter before and after it recomputes control, a forced
    // vehicle never sees the transition that calls Vehicle.ReleaseFlightComputer, so its Auto modes
    // stay. On dispose, a vehicle that the game holds uncontrollable gets that release once, as on
    // the step it would have lost control.
    //
    // Not Vehicle._overrideIsControllable: that debug flag is global and also changes
    // HasControlModule, which SolidRocket and PartFailure read. A KittenEva overrides
    // IsControllable, so it cannot be forced and is refused.
    public static IDisposable ForceControllable(params Vehicle[] vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        foreach (Vehicle vehicle in vehicles)
        {
            ArgumentNullException.ThrowIfNull(vehicle);
            if (vehicle is KittenEva)
                throw new ArgumentException(
                    $"'{vehicle.Id}' is a KittenEva, which overrides Vehicle.IsControllable; it cannot be forced controllable.",
                    nameof(vehicles));
        }
        _releaseFlightComputer ??= AccessTools.Method(typeof(Vehicle), "ReleaseFlightComputer", Type.EmptyTypes)
            ?? throw Drift("Vehicle.ReleaseFlightComputer");
        foreach (Vehicle vehicle in vehicles)
            _forced[vehicle] = _forced.GetValueOrDefault(vehicle) + 1;
        return new ForcedControlScope((Vehicle[])vehicles.Clone());
    }

    private sealed class ForcedControlScope : IDisposable
    {
        private Vehicle[]? _vehicles;

        public ForcedControlScope(Vehicle[] vehicles) => _vehicles = vehicles;

        public void Dispose()
        {
            Vehicle[]? vehicles = _vehicles;
            if (vehicles == null)
                return;
            _vehicles = null;
            foreach (Vehicle vehicle in vehicles)
            {
                int count = _forced.GetValueOrDefault(vehicle) - 1;
                if (count > 0)
                {
                    _forced[vehicle] = count;
                    continue;
                }
                _forced.Remove(vehicle);
                if (!vehicle.IsDisposed && !vehicle.IsControllable)
                    _releaseFlightComputer!.Invoke(vehicle, null);
            }
        }
    }

    #endregion

    #region Rules

    // Sets the given game rules until the scope is disposed, then restores the values it found.
    // Every game check reads Universe.Rules per call, so a change takes effect at the next step.
    public static IDisposable UseRules(bool? radiation = null, bool? lifeSupport = null)
    {
        GameRules rules = Universe.Rules;
        RulesScope scope = new RulesScope(rules.Radiation, rules.LifeSupport);
        if (radiation.HasValue)
            rules.Radiation = radiation.Value;
        if (lifeSupport.HasValue)
            rules.LifeSupport = lifeSupport.Value;
        return scope;
    }

    // The rules this run chose with KSA_HEADLESS_RULES, as the bring-up applied them after the
    // system load. With "settings" these are the start rules of the profile's settings file, which
    // Universe.LoadSystem reads from GameSettings.Current.System.
    public static GameRules GetRunRules()
    {
        if (HeadlessSession.RunRulesKeepSettings)
            return new GameRules
            {
                Radiation = GameSettings.Current.System.StartRadiation,
                LifeSupport = GameSettings.Current.System.StartLifeSupport,
            };
        return new GameRules { Radiation = HeadlessSession.RunRadiation, LifeSupport = HeadlessSession.RunLifeSupport };
    }

    // Puts Universe.Rules back to the run's choice, for example after Universe.DeserializeSave
    // replaced the rules with those of a save.
    public static void RestoreRunRules()
    {
        GameRules run = GetRunRules();
        Universe.Rules.Radiation = run.Radiation;
        Universe.Rules.LifeSupport = run.LifeSupport;
    }

    private sealed class RulesScope : IDisposable
    {
        private readonly bool _radiation;
        private readonly bool _lifeSupport;
        private bool _disposed;

        public RulesScope(bool radiation, bool lifeSupport)
        {
            _radiation = radiation;
            _lifeSupport = lifeSupport;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Universe.Rules.Radiation = _radiation;
            Universe.Rules.LifeSupport = _lifeSupport;
        }
    }

    #endregion

    #region Probe power

    // Each returns the number of probe cores it changed: a probe core is a PowerConsumer with
    // PowerSwitch set, the one Vehicle.ScanControls looks for on a control part.
    public static int SetProbeDraw(Vehicle vehicle, double watts)
    {
        int changed = 0;
        foreach (PowerConsumer consumer in vehicle.Parts.Modules.Get<PowerConsumer>())
        {
            if (!consumer.PowerSwitch)
                continue;
            consumer.Consumed = new Watts((float)watts);
            changed++;
        }
        return changed;
    }

    // Writes the field directly, not PowerConsumer.SetMode, which goes through the editor's
    // symmetry walk (VehicleEditor.ForEachModuleWithSymmetry). Every probe core changes anyway.
    public static int SetProbeMode(Vehicle vehicle, PowerSwitchMode mode)
    {
        int changed = 0;
        foreach (PowerConsumer consumer in vehicle.Parts.Modules.Get<PowerConsumer>())
        {
            if (!consumer.PowerSwitch)
                continue;
            consumer.Mode = mode;
            changed++;
        }
        return changed;
    }

    // The forecast the game uses for the probe power warning (Vehicle.ScanProbeCore): charge, draw
    // and supply of the first probe core's circuit, and the time left. Null without a probe core.
    public static PowerForecast? ProbeForecast(Vehicle vehicle)
    {
        foreach (PowerConsumer consumer in vehicle.Parts.Modules.Get<PowerConsumer>())
        {
            if (consumer.PowerSwitch)
                return PowerForecast.Compute(vehicle.Parts, consumer.Parent.FullPart);
        }
        return null;
    }

    public static PowerForecast ProbeForecast(Vehicle vehicle, Part part) =>
        PowerForecast.Compute(vehicle.Parts, part.FullPart);

    // Fills every battery with Battery.Refill, the game's own fill. The write goes into the part
    // tree's state list, as the editor's charge slider does (VehicleEditor). The solver copies that
    // list into a fresh buffer at the start of each step (ModuleStateful StateUpdater.GetNewStates
    // after VehicleUpdateData.Prepare) and copies the result back on apply, so a write between two
    // SimDriver steps, also before the first one, is what the next step starts from.
    public static int RefillBatteries(Vehicle vehicle)
    {
        PartTree tree = vehicle.Parts;
        Span<Battery> batteries = tree.Modules.Get<Battery>();
        foreach (Battery battery in batteries)
        {
            var slot = tree.Batteries.GetModuleAndAllMutableStatesForInitialization(battery);
            battery.Refill(ref slot.State);
        }
        return batteries.Length;
    }

    // Joules in every battery of the vehicle.
    public static double GetBatteryCharge(Vehicle vehicle)
    {
        PartTree tree = vehicle.Parts;
        double joules = 0.0;
        foreach (Battery battery in tree.Modules.Get<Battery>())
            joules += tree.Batteries.GetState(battery).Charge.Value();
        return joules;
    }

    #endregion

    #region Avionics

    // Avionics only matter while the radiation rule is on: Vehicle.ComputeHold ignores them
    // otherwise. Each returns the number of avionics modules it changed.
    public static int SetAvionicsDose(Vehicle vehicle, double grays)
    {
        Span<Avionics> avionics = vehicle.Parts.Modules.Get<Avionics>();
        foreach (Avionics module in avionics)
            module.DoseGy = grays;
        return avionics.Length;
    }

    // A dose at the tolerance is what Avionics.ComputeHealth reports as Failed.
    public static int FailAvionics(Vehicle vehicle)
    {
        Span<Avionics> avionics = vehicle.Parts.Modules.Get<Avionics>();
        foreach (Avionics module in avionics)
            module.DoseGy = module.ToleranceGy;
        return avionics.Length;
    }

    // Starts the reboot a radiation upset starts in Vehicle.RollUpset, measured from the universe
    // clock that Avionics.IsRebooting is compared with.
    public static int TriggerReboot(Vehicle vehicle) => TriggerReboot(vehicle, Avionics.REBOOT_SECONDS);

    public static int TriggerReboot(Vehicle vehicle, double seconds)
    {
        double until = Universe.GetElapsedSeconds() + seconds;
        Span<Avionics> avionics = vehicle.Parts.Modules.Get<Avionics>();
        foreach (Avionics module in avionics)
            module.RebootUntil = until;
        return avionics.Length;
    }

    #endregion

    #region Seeded upsets

    private static int? _upsetSeed;
    private static int _upsetSeedGeneration;
    private static readonly ConditionalWeakTable<Vehicle, StrongBox<int>> _seededGeneration = new();
    private static UpsetSeedScope? _upsetScope;

    // The seed the upset prefix uses, or null when the game's unseeded Random is kept.
    public static int? UpsetSeed => _upsetSeed;

    // Seeds every vehicle's upset Random from its id and this seed at its next roll, until the
    // scope is disposed and the previous choice returns. Vehicles seeded before are seeded again.
    public static IDisposable SeedUpsets(int seed)
    {
        UpsetSeedScope scope = new UpsetSeedScope(_upsetSeed, _upsetSeedGeneration, _upsetScope);
        _upsetScope = scope;
        SetUpsetSeed(seed);
        return scope;
    }

    private static void SetUpsetSeed(int? seed)
    {
        _upsetSeed = seed;
        _upsetSeedGeneration++;
    }

    private sealed class UpsetSeedScope : IDisposable
    {
        private readonly int? _previous;
        private readonly int _previousGeneration;
        internal readonly UpsetSeedScope? Parent;
        private readonly Dictionary<Vehicle, (Random? Random, bool WasSeeded)> _originals = new(ReferenceEqualityComparer.Instance);
        private bool _disposed;

        public UpsetSeedScope(int? previous, int generation, UpsetSeedScope? parent)
        {
            _previous = previous;
            _previousGeneration = generation;
            Parent = parent;
        }

        internal void Capture(Vehicle vehicle, Random? random, int generation)
        {
            lock (_originals)
                _originals.TryAdd(vehicle, (random, generation == _previousGeneration));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (!ReferenceEquals(_upsetScope, this))
                throw new InvalidOperationException("Dispose upset seed scopes in reverse order.");
            _disposed = true;
            SetUpsetSeed(_previous);
            foreach (var (vehicle, original) in _originals)
            {
                if (vehicle.IsDisposed)
                    continue;
                _upsetRandomField!.SetValue(vehicle, original.Random);
                _seededGeneration.GetOrCreateValue(vehicle).Value = original.WasSeeded ? _upsetSeedGeneration : 0;
            }
            _originals.Clear();
            _upsetScope = Parent;
        }
    }

    internal static void InitializeUpsetSeedFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(SeedEnvVar);
        if (string.IsNullOrWhiteSpace(value))
            SetUpsetSeed(0);
        else if (string.Equals(value.Trim(), "off", StringComparison.OrdinalIgnoreCase))
            SetUpsetSeed(null);
        else if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out int seed))
            SetUpsetSeed(seed);
        else
            throw new InvalidOperationException(
                $"[HeadlessHarness] {SeedEnvVar}='{value}' is neither an integer nor 'off'.");
        HarnessLog.Line($"[bringup] avionics upsets: {(_upsetSeed is int s ? $"seeded with {s}" : "unseeded (game default)")}.");
    }

    // Called by the prefix on Vehicle.RollUpset before the game's own lazy
    // "_upsetRandom ??= new Random()", so the game takes the seeded Random instead.
    internal static void SeedUpsetRandom(Vehicle vehicle)
    {
        if (_upsetSeed is not int seed)
            return;
        StrongBox<int> seeded = _seededGeneration.GetOrCreateValue(vehicle);
        if (seeded.Value == _upsetSeedGeneration)
            return;
        _upsetRandomField ??= BindField("_upsetRandom", typeof(Random));
        for (UpsetSeedScope? scope = _upsetScope; scope != null; scope = scope.Parent)
            scope.Capture(vehicle, (Random?)_upsetRandomField.GetValue(vehicle), seeded.Value);
        _upsetRandomField.SetValue(vehicle, new Random(UpsetSeedFor(vehicle.Id, seed)));
        seeded.Value = _upsetSeedGeneration;
    }

    // FNV-1a over the id, mixed with the seed. string.GetHashCode is randomized per process, so it
    // would not repeat across runs.
    internal static int UpsetSeedFor(string vehicleId, int seed)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in vehicleId)
            {
                hash = (hash ^ c) * 16777619;
            }
            hash ^= (uint)seed * 2654435769u;
            hash ^= hash >> 16;
            hash *= 2246822507u;
            hash ^= hash >> 13;
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    #endregion

    #region Missions

    // The mission plan of the vehicle's part tree, which Vehicle.UpdatePerFrameData records progress
    // on (MissionProgress.Record) and the life support forecast plans supplies with. The plan is
    // stored as given, as MissionPicker does; null removes it.
    public static void SetMission(Vehicle vehicle, MissionPlan? mission) => vehicle.Parts.Mission = mission;

    public static MissionPlan? GetMission(Vehicle vehicle) => vehicle.Parts.Mission;

    #endregion

    #region Hygiene

    // TimedAlert keeps every alert in a static list that only the UI draw prunes, so headless it
    // grows for the whole run.
    public static void ClearAlerts() => TimedAlert.ClearAll();

    // Universe.ResetSimulationSpeed stops an auto warp and sets the speed to 1. Loss of control and
    // the probe power warning set it to 1 as well (Vehicle.UpdateControl, UpdateProbePowerWarning).
    public static void ResetSimulationSpeed() => Universe.ResetSimulationSpeed();

    #endregion

    // Binds every reflection key of this file, so a self-test can report a renamed member without
    // a vehicle.
    internal static void BindControlKeys()
    {
        _controlField ??= BindField("_control", typeof(VehicleControl));
        _upsetRandomField ??= BindField("_upsetRandom", typeof(Random));
        _releaseFlightComputer ??= AccessTools.Method(typeof(Vehicle), "ReleaseFlightComputer", Type.EmptyTypes)
            ?? throw Drift("Vehicle.ReleaseFlightComputer");
    }

    private static FieldInfo BindField(string name, Type type)
    {
        FieldInfo field = AccessTools.Field(typeof(Vehicle), name) ?? throw Drift($"Vehicle.{name}");
        if (field.FieldType != type)
            throw Drift($"Vehicle.{name} of type {type.Name}");
        return field;
    }

    private static PowerConsumer? FindProbeConsumer(Part part)
    {
        foreach (PowerConsumer consumer in part.Modules.Get<PowerConsumer>())
        {
            if (consumer.PowerSwitch)
                return consumer;
        }
        return null;
    }

    private static T? FirstOrNull<T>(Span<T> span) where T : class => span.IsEmpty ? null : span[0];

    private static InvalidOperationException Drift(string what) =>
        new($"[HeadlessHarness] {what} not found - game version may have changed.");
}

// What Vehicle.ComputeControl is expected to answer after the next step, from the pod crew and the
// probe core power that PredictControl found. CrewSeated and BatteriesRefilled count what
// EnsureControllable did and are 0 from PredictControl.
public readonly record struct ControlExpectation(
    bool Expected,
    VehicleControl Predicted,
    CommandPodCrew Pod,
    ProbeCorePower Probe,
    int CrewSeated,
    int BatteriesRefilled,
    string Reason);
