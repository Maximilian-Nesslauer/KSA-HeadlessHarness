using System.Collections.Generic;
using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Builds each canned VehicleBuilder design from stock parts, spawns it in orbit with SpawnTree and
// checks what a test relies on: the part count, the mass the tree predicted, the crew, the
// sequence layout and that the vehicle is controllable after one step. The rocket is then staged
// twice through VehicleSpawner.Stage (ignition, then the split of the lower stage), and the RCS
// blocks of the RCS craft must sit on the tank surface facing inward. Skips when a stock part is
// not loaded.
public sealed class SpawnBuilderTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;
    private const double MassTol = 1e-3;      // relative, spawned vehicle against the built tree
    private const double MountTol = 1e-6;     // m and unit-vector components

    public string Name => "spawn-builder";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[spawn-builder] SKIP: the loaded system has no home body.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        PartTree? rocket = null, probe = null, rcs = null;
        try
        {
            try
            {
                rocket = VehicleBuilder.TwoStageRocket();
                probe = VehicleBuilder.ProbeCraft();
                rcs = VehicleBuilder.RcsCraft();
            }
            catch (InvalidOperationException e) when (e.Message.Contains("is not loaded", StringComparison.Ordinal))
            {
                HarnessLog.Line($"[spawn-builder] SKIP: {e.Message}");
                return 0;
            }

            ok &= CheckRcsMounts(rcs);
            ok &= CheckDesign(system, home, driver, "rocket", rocket, 8, crewed: true, propellant: true, out Vehicle rocketVehicle);
            ok &= CheckRocketSequences(rocketVehicle);
            ok &= CheckStaging(driver, rocketVehicle);
            TestSupport.DespawnNewVehicles(system, preexisting);

            ok &= CheckDesign(system, home, driver, "probe", probe, 2, crewed: false, propellant: false, out _);
            TestSupport.DespawnNewVehicles(system, preexisting);

            ok &= CheckDesign(system, home, driver, "rcs", rcs, 6, crewed: false, propellant: true, out _);
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
            // A design that never reached a vehicle still owns its tree.
            foreach (PartTree? tree in new[] { rocket, probe, rcs })
            {
                if (tree is { IsDisposed: false, OwningVehicle: null })
                    tree.Dispose();
            }
        }

        HarnessLog.Line($"[spawn-builder] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckDesign(CelestialSystem system, IParentBody home, SimDriver driver, string label, PartTree tree,
        int expectedParts, bool crewed, bool propellant, out Vehicle vehicle)
    {
        double predicted = tree.ComputeInertMassPropertiesAsmb().Props.Mass + tree.ComputePropellantMassPropertiesAsmb().Props.Mass;
        Dictionary<string, int> missionCounts = new();
        foreach (KittenRosterEntryData kitten in Universe.KittenRoster.Kittens)
            missionCounts[kitten.Name] = kitten.MissionCount;
        Orbit orbit = VehicleSpawner.CircularAtAltitude(home, SpawnAltitudeM, Universe.GetElapsedTime());
        vehicle = VehicleSpawner.SpawnTree(tree, system, home, $"HarnessBuilt-{label}", orbit);

        int parts = vehicle.Parts.Count;
        double mass = vehicle.TotalMass;
        double massErr = predicted > 0.0 ? Math.Abs(mass - predicted) / predicted : double.NaN;
        int seats = vehicle.SeatCount;
        int crew = CountCrew(vehicle);
        bool missionsOk = true;
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            KittenRosterEntryData? kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash);
            if (kitten != null)
                missionsOk &= kitten.MissionCount == missionCounts.GetValueOrDefault(kitten.Name) + 1;
        }

        driver.Step(0.1);
        bool controllable = !vehicle.IsDisposed && vehicle.IsControllable;

        bool partsOk = parts == expectedParts;
        bool massOk = massErr < MassTol;
        bool propellantOk = propellant ? vehicle.PropellantMass > 0f : vehicle.PropellantMass == 0f;
        bool crewOk = crewed ? seats > 0 && crew == seats : seats == 0;
        bool ok = partsOk && massOk && propellantOk && crewOk && controllable && missionsOk;
        HarnessLog.Line($"[spawn-builder] {label}: parts={parts} (expect {expectedParts}) mass={mass:F1}kg (tree {predicted:F1}kg, err {massErr:E2}) " +
                        $"inert={vehicle.InertMass:F1}kg prop={vehicle.PropellantMass:F1}kg crew={crew}/{seats} " +
                        $"controllable={controllable} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool CheckRocketSequences(Vehicle rocket)
    {
        Dictionary<int, string> expected = new()
        {
            [1] = VehicleBuilder.StockParts.SeaLevelEngine,
            [2] = VehicleBuilder.StockParts.Interstage3W3H,
            [3] = VehicleBuilder.StockParts.VacuumEngine,
            [4] = VehicleBuilder.StockParts.CargoBay,
        };
        List<string> layout = new List<string>();
        bool ok = rocket.Parts.SequenceList.Count == expected.Count;
        foreach (Sequence sequence in rocket.Parts.SequenceList.Sequences)
        {
            List<string> ids = new List<string>();
            foreach (Part part in sequence.Parts)
                ids.Add(part.Template.Id);
            layout.Add($"{sequence.Number}: {string.Join(" + ", ids)}");
            ok &= expected.TryGetValue(sequence.Number, out string? want) && ids.Count == 1 && ids[0] == want;
        }
        HarnessLog.Line($"[spawn-builder] rocket sequences: {string.Join("; ", layout)} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    // Sequence 1 only ignites (throttle stays 0, so nothing moves), sequence 2 splits off the
    // interstage with the lower tank and engine.
    private static bool CheckStaging(SimDriver driver, Vehicle rocket)
    {
        bool clip = driver.ClipToGameStepLimit;
        driver.ClipToGameStepLimit = true;
        UniverseTime start = driver.Elapsed;
        Universe.LimitNextStep(start + 0.025);
        IReadOnlyList<Vehicle> first = VehicleSpawner.Stage(driver, rocket);
        bool settled = Math.Abs((driver.Elapsed - start).Seconds() - 0.5) < 1e-9;
        driver.ClipToGameStepLimit = clip;
        IReadOnlyList<Vehicle> second = VehicleSpawner.Stage(driver, rocket);
        int shedParts = second.Count == 1 ? second[0].Parts.Count : -1;
        bool ok = settled && first.Count == 0 && second.Count == 1 && shedParts == 3 && rocket.Parts.Count == 5
                  && rocket.Parts.SequenceList.GetNextSequenceNumber() == 3;
        HarnessLog.Line($"[spawn-builder] rocket staging: sequence 1 made {first.Count} vehicle(s), sequence 2 made {second.Count} " +
                        $"with {shedParts} part(s), rocket keeps {rocket.Parts.Count} part(s), next sequence " +
                        $"{rocket.Parts.SequenceList.GetNextSequenceNumber()} => {TestSupport.Verdict(ok)}");
        Orbit freshOrbit = VehicleSpawner.CircularAtAltitude(rocket.Orbit.Parent, SpawnAltitudeM + 50_000, driver.Elapsed);
        Vehicle fresh = VehicleSpawner.SpawnTree(VehicleBuilder.TwoStageRocket(), rocket.System, rocket.Orbit.Parent,
            "HarnessFreshDecoupler", freshOrbit);
        fresh.Parts.EnsureDerived(DerivedData.Sequences);
        fresh.Parts.SequenceList.SetActiveSequence(1);
        bool noBubble = TestSupport.GetPhysicsBubble(fresh) == null;
        IReadOnlyList<Vehicle> freshStages = VehicleSpawner.Stage(driver, fresh);
        bool freshOk = noBubble && freshStages.Count == 1 && fresh.Parts.Count == 5;
        HarnessLog.Line($"[spawn-builder] fresh decoupler-first staging => {TestSupport.Verdict(freshOk)}");
        ok &= freshOk;
        return ok;
    }

    // Every RCS block's connector must sit on one of the mount points, 0.5 m off the tank axis,
    // with its +X pointing into the tank and a connection to it.
    private static bool CheckRcsMounts(PartTree rcs)
    {
        Part? tank = null;
        foreach (Part part in rcs.Parts)
        {
            if (part.Template.Id == VehicleBuilder.StockParts.MonopropTank1W)
                tank = part;
        }
        if (tank == null)
        {
            HarnessLog.Line("[spawn-builder] rcs mounts: the tank is missing => FAIL");
            return false;
        }

        int blocks = 0;
        bool ok = true;
        HashSet<int> used = new HashSet<int>();
        foreach (Part part in rcs.Parts)
        {
            if (part.Template.Id != VehicleBuilder.StockParts.RcsBlock)
                continue;
            blocks++;
            Part.Connector connector = VehicleBuilder.FindConnector(part, "_connector7");
            double3 position = connector.PositionVehicleAsmb;
            double3 outward = VehicleBuilder.Rotate(double3.UnitX, connector.Asmb2VehicleAsmb);
            int match = -1;
            for (int i = 0; i < VehicleBuilder.RcsNormals.Length; i++)
            {
                double3 normal = VehicleBuilder.Rotate(VehicleBuilder.RcsNormals[i], tank.Asmb2VehicleAsmb);
                double3 point = tank.PositionVehicleAsmb + normal * VehicleBuilder.RcsMountRadius;
                if ((position - point).Length() < MountTol && (outward + normal).Length() < MountTol)
                    match = i;
            }
            bool connected = connector.Connection != null;
            ok &= match >= 0 && used.Add(match) && connected;
            if (match < 0 || !connected)
                HarnessLog.Line($"[spawn-builder] rcs block at ({position.X:F4}, {position.Y:F4}, {position.Z:F4}) facing " +
                                $"({outward.X:F4}, {outward.Y:F4}, {outward.Z:F4}) connected={connected} matches no mount point.");
        }
        ok &= blocks == VehicleBuilder.RcsNormals.Length;
        HarnessLog.Line($"[spawn-builder] rcs mounts: {blocks} block(s), {used.Count} on distinct mount points => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static int CountCrew(Vehicle vehicle)
    {
        int crew = 0;
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            KittenRosterEntryData? kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash);
            if (seat.AssignedKittenHash != KeyHash.Zero && kitten != null && kitten.AssignedVehicleId == vehicle.Id)
                crew++;
        }
        return crew;
    }
}
