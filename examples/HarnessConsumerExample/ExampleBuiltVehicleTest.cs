using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The worked example of a vehicle built in code: assemble stock parts with VehicleBuilder, spawn
// the tree in orbit with SpawnTree, and stage it with VehicleSpawner.Stage, which returns the
// stage a decoupler shed. The same tree spawns on the ground with VehicleSpawner.SpawnAtLocation
// or SpawnAtLaunchSite. Opt-in, because the harness self-test spawn-builder runs the same path in
// every default suite.
public sealed class ExampleBuiltVehicleTest : IHarnessTest
{
    public string Name => "example-built-vehicle";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[example-built-vehicle] SKIP: the loaded system has no home body.");
            return 0;
        }

        // Connector keys are the Key attributes of the part's connectors in the part data. A wrong
        // key throws with the part's real keys, which is the quickest way to find them.
        VehicleBuilder builder = new VehicleBuilder(VehicleBuilder.StockParts.ProbeCoreSmall);
        Part tank = builder.AddStacked(builder.Root, "_connector44", VehicleBuilder.StockParts.Tank3W3H, "_connector25");
        Part engine = builder.AddStacked(tank, "_connector39", VehicleBuilder.StockParts.VacuumEngine, "_connector5");
        builder.Sequence(engine, 1);
        PartTree tree = builder.Build(fillTanks: true);

        // Sweep everything that appears from here on, so a vehicle a failed spawn left behind and
        // every stage a split made are torn down too.
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        try
        {
            Orbit orbit = VehicleSpawner.CircularAtAltitude(home, 400_000.0, Universe.GetElapsedTime());
            Vehicle vehicle = VehicleSpawner.SpawnTree(tree, system, home, "ExampleBuiltProbe", orbit,
                SpawnOptions.Default with { MakeIdUnique = true });
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            TestSupport.SetManualControlInputs(vehicle, 1f, engineOn: true);

            double m0 = vehicle.TotalMass;
            IReadOnlyList<Vehicle> shed = VehicleSpawner.Stage(driver, vehicle, settleSeconds: 2.0);
            driver.Step(0.1, 30);
            double burned = m0 - vehicle.TotalMass;

            bool ok = shed.Count == 0 && burned > 0.0;
            HarnessLog.Line($"[example-built-vehicle] '{vehicle.Id}': {vehicle.Parts.Count} part(s), burned {burned:F1} kg in 5 s " +
                            $"=> {TestSupport.Verdict(ok)}");
            return ok ? 0 : 1;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }
}
