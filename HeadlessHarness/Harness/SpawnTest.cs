using System.Collections.Generic;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Verifies safe vehicle copying: clone a crewed live vehicle into a FRESH part tree on a chosen
// circular orbit above its own, confirm the copy reports that orbit, and confirm the copy has crew
// of its own: no kitten sits in both vehicles, every kitten of the copy is assigned to the copy,
// and the source keeps its crew. The source is the first crewed vehicle of the loaded system, or
// the crewed rocket VehicleBuilder.TwoStageRocket builds when the system has none. Skips when
// neither exists.
public sealed class SpawnTest : IHarnessTest
{
    private const double AltitudeOffsetM = 500_000.0; // spawn the copy clear of the source orbit
    private const double SourceAltitudeM = 500_000.0;
    private const double SmaTol = 1e-3;               // relative
    private const double EccentricityTol = 1e-3;

    public string Name => "spawn";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        // Snapshot-and-sweep rather than despawning the returned vehicles: a spawn helper that
        // throws partway may already have a live vehicle in the system.
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        try
        {
            Vehicle? source = FindCrewedVehicle(system) ?? SpawnSource(session, system);
            if (source == null)
            {
                HarnessLog.Line("[spawn] SKIP: the loaded system has no crewed vehicle to copy and no home body to build one at.");
                return 0;
            }

            IParentBody parent = source.Orbit.Parent;
            double radius = source.Orbit.SemiMajorAxis + AltitudeOffsetM;
            UniverseTime now = Universe.GetElapsedTime();
            Orbit target = VehicleSpawner.CircularCci(parent, radius, now);
            List<KittenRosterEntryData> sourceCrew = Crew(source);

            Vehicle spawned = VehicleSpawner.SpawnCopy(source, parent, "HarnessTestSat", target);
            Orbit o = spawned.Orbit;
            bool smaOk = Math.Abs(o.SemiMajorAxis - radius) / radius < SmaTol;
            bool eccOk = o.Eccentricity < EccentricityTol;
            bool orbitOk = smaOk && eccOk;
            HarnessLog.Line($"[spawn] '{spawned.Id}' (copy of '{source.Id}') mass={spawned.TotalMass:F0}kg " +
                            $"SMA={o.SemiMajorAxis:E4} (target {radius:E4}) ecc={o.Eccentricity:F5} => {TestSupport.Verdict(orbitOk)}");

            bool crewOk = CheckCrew(source, sourceCrew, spawned);
            return orbitOk && crewOk ? 0 : 1;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }

    private static bool CheckCrew(Vehicle source, List<KittenRosterEntryData> sourceCrew, Vehicle copy)
    {
        HashSet<uint> sourceSeats = SeatHashes(source);
        HashSet<uint> copySeats = SeatHashes(copy);
        int shared = 0;
        foreach (uint hash in copySeats)
        {
            if (sourceSeats.Contains(hash))
                shared++;
        }
        List<KittenRosterEntryData> copyCrew = Crew(copy);
        bool sourceKept = true;
        foreach (KittenRosterEntryData kitten in sourceCrew)
            sourceKept &= kitten.AssignedVehicleId == source.Id;
        bool ok = shared == 0 && copyCrew.Count == copy.SeatCount && copyCrew.Count == copySeats.Count
                  && sourceKept && Crew(source).Count == sourceCrew.Count;
        HarnessLog.Line($"[spawn] crew: source {sourceCrew.Count}/{source.SeatCount}, copy {copyCrew.Count}/{copy.SeatCount}, " +
                        $"{shared} kitten(s) in both, source kept its crew={sourceKept} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static Vehicle? FindCrewedVehicle(CelestialSystem system)
    {
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is Vehicle v and not KittenEva && v.SeatCount > 0 && Crew(v).Count > 0)
                return v;
        }
        return null;
    }

    private static Vehicle? SpawnSource(HeadlessSession session, CelestialSystem system)
    {
        if (system.HomeBody is not IParentBody home)
            return null;
        Orbit orbit = VehicleSpawner.CircularAtAltitude(home, SourceAltitudeM, Universe.GetElapsedTime());
        Vehicle source = VehicleSpawner.SpawnTree(VehicleBuilder.TwoStageRocket(), system, home, "HarnessSpawnSource", orbit);
        // One step gives the source a physics bubble, which SpawnCopy joins the copy to.
        session.CreateDriver().Step(0.1);
        HarnessLog.Line($"[spawn] the system has no crewed vehicle; copying the built rocket '{source.Id}'.");
        return source;
    }

    private static HashSet<uint> SeatHashes(Vehicle vehicle)
    {
        HashSet<uint> hashes = new HashSet<uint>();
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash != KeyHash.Zero)
                hashes.Add(seat.AssignedKittenHash.Code);
        }
        return hashes;
    }

    private static List<KittenRosterEntryData> Crew(Vehicle vehicle)
    {
        List<KittenRosterEntryData> crew = new List<KittenRosterEntryData>();
        foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            KittenRosterEntryData? kitten = seat.AssignedKittenHash != KeyHash.Zero ? Universe.KittenRoster.Find(seat.AssignedKittenHash) : null;
            if (kitten != null && kitten.AssignedVehicleId == vehicle.Id)
                crew.Add(kitten);
        }
        return crew;
    }
}
