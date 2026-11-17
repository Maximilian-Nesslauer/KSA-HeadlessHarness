using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// The vehicle saves bundled with the harness must resolve to the harness bundle, ahead of any copy
// in the game's Vehicles folder, and spawn crewed. The consumer scan is checked on a scratch mods
// folder under HarnessLog.DataDirectory: a mod that names the harness in its mod.toml contributes
// its saves, a mod that does not is ignored, and a name the harness bundle has already stays with
// the harness. Skips a save whose parts the loaded content lacks.
public sealed class SpawnBundledTest : IHarnessTest
{
    private static readonly string[] BundledNames = { "Test Vehicle 1", "Test Vehicle 2" };
    private const string ScratchSaveName = "Harness Bundle Scan";
    private const double SpawnAltitudeM = 500_000.0;

    public string Name => "spawn-bundled";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[spawn-bundled] SKIP: the loaded system has no home body.");
            return 0;
        }

        bool ok = true;
        IReadOnlyList<string> resolved = TestSupport.ResolveVehicleSaves(BundledNames);
        ok &= resolved.Count == BundledNames.Length;

        Orbit orbit = VehicleSpawner.CircularAtAltitude(home, SpawnAltitudeM, Universe.GetElapsedTime());
        foreach (string name in BundledNames)
        {
            VehicleSaveSource? source = VehicleSpawner.FindVehicleSave(name);
            if (source == null || source.Origin != VehicleSaveOrigin.HarnessBundle)
            {
                HarnessLog.Line($"[spawn-bundled] '{name}' resolves to {source?.Describe() ?? "nothing"}, not to the harness bundle => FAIL");
                ok = false;
                continue;
            }

            HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
            try
            {
                Vehicle vehicle = VehicleSpawner.SpawnFromSave(name, system, home, "HarnessBundled", orbit);
                int crew = 0;
                foreach (IVASeat seat in vehicle.Parts.Modules.Get<IVASeat>())
                {
                    if (seat.AssignedKittenHash != KeyHash.Zero && Universe.KittenRoster.Find(seat.AssignedKittenHash)?.AssignedVehicleId == vehicle.Id)
                        crew++;
                }
                bool spawned = vehicle.Parts.Count > 1 && vehicle.SeatCount > 0 && crew == vehicle.SeatCount
                               && vehicle.Parts.SequenceList.GetNextSequenceNumber() == 1;
                HarnessLog.Line($"[spawn-bundled] '{name}' from the harness bundle: {vehicle.Parts.Count} part(s), mass={vehicle.TotalMass:F0}kg, " +
                                $"crew {crew}/{vehicle.SeatCount}, next sequence {vehicle.Parts.SequenceList.GetNextSequenceNumber()} => {TestSupport.Verdict(spawned)}");
                ok &= spawned;
            }
            catch (InvalidOperationException e) when (e.Message.Contains("missing or failed to load", StringComparison.Ordinal))
            {
                HarnessLog.Line($"[spawn-bundled] SKIP '{name}': {e.Message}");
            }
            finally
            {
                TestSupport.DespawnNewVehicles(system, preexisting);
            }
        }

        ok &= CheckConsumerScan();
        HarnessLog.Line($"[spawn-bundled] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckConsumerScan()
    {
        VehicleSaveSource? template = VehicleSpawner.FindVehicleSave(BundledNames[0]);
        if (template == null || template.Origin != VehicleSaveOrigin.HarnessBundle)
        {
            HarnessLog.Line("[spawn-bundled] consumer scan: no harness bundle to copy a save from => FAIL");
            return false;
        }

        string root = Path.Combine(HarnessLog.DataDirectory, $"bundle-scan-{Environment.ProcessId}");
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            WriteMod(root, "ConsumerA", "[[StarMap.ModDependencies]]\nModId = \"HeadlessHarness\"\n", template, ScratchSaveName);
            WriteMod(root, "ConsumerB", "[[StarMap.ModDependencies]]\nModId = \"HeadlessHarness\"\n", template, BundledNames[0]);
            WriteMod(root, "Unrelated", "name = \"Unrelated\"\n", template, "Unrelated Save");

            List<VehicleSaveSource> found = new List<VehicleSaveSource>(VehicleSpawner.BundledVehicleSaves);
            int before = found.Count;
            VehicleSpawner.ScanConsumerBundles(root, null, "HeadlessHarness", found);
            List<VehicleSaveSource> added = found.GetRange(before, found.Count - before);

            bool ok = added.Count == 1 && added[0].Name == ScratchSaveName && added[0].Origin == VehicleSaveOrigin.ConsumerBundle
                      && added[0].ModName == "ConsumerA";
            bool read = ok && added[0].ReadData().RootPartInstance != null;
            ok &= read;
            List<string> names = added.ConvertAll(s => $"'{s.Name}' ({s.ModName})");
            HarnessLog.Line($"[spawn-bundled] consumer scan added {added.Count} save(s): {string.Join(", ", names)}; " +
                            $"root part read={read} => {TestSupport.Verdict(ok)}");
            return ok;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception e)
            {
                HarnessLog.Line($"[spawn-bundled] could not remove the scratch folder '{root}': {e.Message}");
            }
        }
    }

    private static void WriteMod(string root, string modName, string tomlBody, VehicleSaveSource template, string saveName)
    {
        string modDir = Path.Combine(root, modName);
        string saveDir = Path.Combine(modDir, VehicleSpawner.BundledVehiclesFolderName, saveName);
        Directory.CreateDirectory(saveDir);
        File.WriteAllText(Path.Combine(modDir, "mod.toml"), tomlBody);
        File.Copy(Path.Combine(template.Folder, "vehicle.xml"), Path.Combine(saveDir, "vehicle.xml"));
        List<string> meta = new List<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(template.Folder, "meta.toml")))
            meta.Add(line.StartsWith("name", StringComparison.Ordinal) ? $"name = \"{saveName}\"" : line);
        File.WriteAllLines(Path.Combine(saveDir, "meta.toml"), meta);
    }
}
