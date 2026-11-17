using System.Collections.Generic;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

public static partial class VehicleSpawner
{
    // Presses the staging key: SequenceList.ActivateNextSequence, the call the in-game key makes,
    // then steps settleSeconds of sim time at dt (at least one step, because the activation only
    // enqueues and takes effect in the next step's input drain). Returns every vehicle that was not
    // in the system before, which is what a decoupler split registers, in system order. An empty
    // list means the sequence ignited or toggled something without a split, or the sequence list was
    // spent already. Despawn the returned vehicles, or sweep them with TestSupport.DespawnNewVehicles.
    public static IReadOnlyList<Vehicle> Stage(SimDriver driver, Vehicle vehicle, double settleSeconds = 0.5, double dt = 0.1)
    {
        if (!double.IsFinite(dt) || dt <= 0.0)
            throw new ArgumentException($"dt must be positive, got {dt}.");
        if (!double.IsFinite(settleSeconds) || settleSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(settleSeconds));
        CelestialSystem system = vehicle.System;
        vehicle.Parts.EnsureDerived(DerivedData.Sequences);
        SequenceList sequences = vehicle.Parts.SequenceList;
        int next = sequences.GetNextSequenceNumber();
        if (next == -1)
        {
            HarnessLog.Line($"[harness] '{vehicle.Id}' has no sequence left to activate.");
            return Array.Empty<Vehicle>();
        }

        HashSet<string> before = TestSupport.CollectVehicleIds(system);
        if (TestSupport.GetPhysicsBubble(vehicle) == null)
        {
            driver.Step(dt);
            if (TestSupport.GetPhysicsBubble(vehicle) == null)
                throw new InvalidOperationException($"'{vehicle.Id}' did not join a physics bubble before staging.");
        }
        sequences.ActivateNextSequence(vehicle);
        if (settleSeconds == 0.0)
            driver.Step(dt);
        else
            driver.StepFor(settleSeconds, dt);

        List<Vehicle> created = new List<Vehicle>();
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle v && !before.Contains(v.Id))
                created.Add(v);
        }
        HarnessLog.Line($"[harness] '{vehicle.Id}' activated sequence {next}: {created.Count} new vehicle(s)" +
                        (created.Count > 0 ? $" ({string.Join(", ", created.ConvertAll(v => v.Id))})" : "") + ".");
        return created;
    }
}
