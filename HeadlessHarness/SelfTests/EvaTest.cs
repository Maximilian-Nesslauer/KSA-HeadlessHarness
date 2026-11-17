using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// A kitten leaves a crewed command pod in orbit (Eva.SpawnEva, through EVADoor.PerformEvaForSeat),
// floats for a second, boards again (Eva.Board, through KittenEva.BoardVehicle), then goes out a
// second time and is despawned. Checks the crew count of the pod and the kitten's roster entry at
// each step, and that Program.ControlledVehicle is what it was before. Opt-in. Skips when the
// command pod is not loaded.
public sealed class EvaTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;
    private const double Dt = 0.1;

    public string Name => "eva";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[eva] SKIP: the loaded system has no home body.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        Vehicle? controlledBefore = Program.ControlledVehicle;
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        try
        {
            PartTree podTree;
            try
            {
                podTree = new VehicleBuilder(VehicleBuilder.StockParts.CommandPod).Build();
            }
            catch (InvalidOperationException e) when (e.Message.Contains("is not loaded", StringComparison.Ordinal))
            {
                HarnessLog.Line($"[eva] SKIP: {e.Message}");
                return 0;
            }
            Orbit orbit = VehicleSpawner.CircularAtAltitude(home, SpawnAltitudeM, Universe.GetElapsedTime());
            Vehicle pod = VehicleSpawner.SpawnTree(podTree, system, home, "HarnessEvaPod", orbit);
            driver.Step(Dt);

            int seats = pod.SeatCount;
            int crewBefore = CountCrew(pod);
            IVASeat seat = Eva.FindCrewedSeat(pod) ?? throw new InvalidOperationException("the pod has no crewed seat.");
            KittenRosterEntryData kitten = Universe.KittenRoster.Find(seat.AssignedKittenHash)!;

            KittenEva eva = Eva.SpawnEva(driver, pod, seat, Dt);
            bool outOk = !eva.IsDisposed && eva.Id == kitten.Name && CountCrew(pod) == crewBefore - 1
                         && kitten.AssignedVehicleId == Eva.EvaVehicleId && ReferenceEquals(Program.ControlledVehicle, controlledBefore);
            HarnessLog.Line($"[eva] out: '{eva.Id}' crew {CountCrew(pod)}/{seats} (was {crewBefore}), roster '{kitten.AssignedVehicleId}', " +
                            $"distance {(eva.GetPositionCce() - pod.GetPositionCce()).Length():F2} m => {TestSupport.Verdict(outOk)}");
            ok &= outOk;

            SaveRoundTrip.Result savedEva = SaveRoundTrip.Vehicle(eva);
            if (!savedEva.Equal)
            {
                SaveRoundTrip.LogXmlPair("eva", savedEva);
                HarnessLog.Line($"[eva] save difference: {savedEva.FirstDifference}");
            }
            bool evaRecord = savedEva.Equal && !string.IsNullOrEmpty(Fingerprint.ReadSaveXml(savedEva.BeforeXml).Character);
            ok &= evaRecord;
            HarnessLog.Line($"[eva] EVA save round trip retains its character => {TestSupport.Verdict(evaRecord)}");

            driver.Step(Dt, 10);
            bool aliveOk = !eva.IsDisposed && !pod.IsDisposed;
            HarnessLog.Line($"[eva] after 1 s: kitten alive={!eva.IsDisposed}, distance {(eva.GetPositionCce() - pod.GetPositionCce()).Length():F2} m => {TestSupport.Verdict(aliveOk)}");
            ok &= aliveOk;

            CrewSeatResult boarded = Eva.Board(eva, pod);
            driver.Step(Dt);
            bool boardOk = boarded == CrewSeatResult.Seated && eva.IsDisposed && CountCrew(pod) == crewBefore
                           && kitten.AssignedVehicleId == pod.Id && system.All.Get(kitten.Name) == null
                           && ReferenceEquals(Program.ControlledVehicle, controlledBefore);
            HarnessLog.Line($"[eva] board: {boarded}, crew {CountCrew(pod)}/{seats}, roster '{kitten.AssignedVehicleId}' => {TestSupport.Verdict(boardOk)}");
            ok &= boardOk;

            IVASeat again = Eva.FindCrewedSeat(pod) ?? throw new InvalidOperationException("the pod lost its crew on boarding.");
            KittenRosterEntryData second = Universe.KittenRoster.Find(again.AssignedKittenHash)!;
            KittenEva secondEva = Eva.SpawnEva(driver, pod, again, Dt);
            VehicleSpawner.Despawn(secondEva);
            driver.Step(Dt);
            bool despawnOk = secondEva.IsDisposed && !second.AssignedToVehicle && system.All.Get(second.Name) == null;
            HarnessLog.Line($"[eva] despawn on EVA: '{second.Name}' disposed={secondEva.IsDisposed}, still assigned={second.AssignedToVehicle} => {TestSupport.Verdict(despawnOk)}");
            ok &= despawnOk;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
            Program.ControlledVehicle = controlledBefore is { IsDisposed: false } ? controlledBefore : null;
        }

        HarnessLog.Line($"[eva] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static int CountCrew(Vehicle vehicle)
    {
        int crew = 0;
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash != KeyHash.Zero && Universe.KittenRoster.Find(seat.AssignedKittenHash)?.AssignedVehicleId == vehicle.Id)
                crew++;
        }
        return crew;
    }
}
