using System.Collections.Generic;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Where a vehicle save the harness spawns came from.
public enum VehicleSaveOrigin
{
    // The vehicles folder next to the harness DLL.
    HarnessBundle,

    // The vehicles folder next to the mod.toml of a consumer mod.
    ConsumerBundle,

    // The game's Vehicles folder of the profile the run uses.
    GameVehiclesFolder,
}

// One vehicle save the harness can spawn. A bundled save is read straight from its folder and is
// never registered with the game's save lists, so it neither shows up in the player's Vehicles
// window nor replaces a player save of the same name. A save from the game's Vehicles folder reads
// through the game's own VehicleSave, which caches the parsed file the same way.
public sealed class VehicleSaveSource
{
    private readonly VehicleSave? _gameSave;
    private VehicleSaveData? _data;

    internal VehicleSaveSource(string name, VehicleSaveOrigin origin, string folder, string? modName, VehicleSave? gameSave)
    {
        Name = name;
        Origin = origin;
        Folder = folder;
        ModName = modName;
        _gameSave = gameSave;
    }

    // The save name, the Name of its meta.toml, which is also the name the game lists it under.
    public string Name { get; }

    public VehicleSaveOrigin Origin { get; }

    // The folder that holds the save's meta.toml and vehicle.xml.
    public string Folder { get; }

    // The mod folder name of a bundled save; null for a save from the game's Vehicles folder.
    public string? ModName { get; }

    public string Describe() => Origin switch
    {
        VehicleSaveOrigin.HarnessBundle => $"the harness bundle ({Folder})",
        VehicleSaveOrigin.ConsumerBundle => $"the bundle of mod '{ModName}' ({Folder})",
        _ => $"the game's Vehicles folder ({Folder})",
    };

    // The parsed save. A bundled file is read the way VehicleSaveData.LoadFrom reads one, with the
    // game's own serializer (VehicleSaves.VehicleSerializer) followed by OnDataLoad, and cached like
    // UncompressedVehicleSave caches it. The game catches a broken vehicle file only in its own load
    // UI, so the rethrow names the save and a caller can skip or fail that one save.
    public VehicleSaveData ReadData()
    {
        if (_data != null)
            return _data;
        try
        {
            _data = _gameSave != null ? _gameSave.VehicleSaveData : ReadBundledFile();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"vehicle save '{Name}' could not be read: {e.Message}", e);
        }
        return _data;
    }

    private VehicleSaveData ReadBundledFile()
    {
        string path = Path.Combine(Folder, VehicleSpawner.VehicleFileName);
        using StreamReader reader = new StreamReader(path);
        VehicleSaveData data = VehicleSaves.VehicleSerializer.Deserialize(reader) as VehicleSaveData
            ?? throw new InvalidOperationException($"'{path}' holds no vehicle data.");
        data.OnDataLoad(KSA.Mod.Empty);
        return data;
    }
}

// Resolves vehicle saves by name: the vehicles bundled with the harness first, then the vehicles
// bundled with each consumer mod, then the game's Vehicles folder. A bundled save therefore gives
// the same vehicle on every machine, whatever the player keeps in the profile.
public static partial class VehicleSpawner
{
    // The folder name a mod bundles its vehicle saves in, next to its mod.toml. Each save is a
    // subfolder with the meta.toml and vehicle.xml the game writes for a vehicle save.
    public const string BundledVehiclesFolderName = "vehicles";

    // The file names UncompressedVehicleSave and SaveMetaData use inside a save folder.
    internal const string VehicleFileName = "vehicle.xml";

    private static List<VehicleSaveSource>? _bundledSaves;

    // Every bundled save, in resolution order. Scanned once on first use; a mod deployed while the
    // run is up is seen after RefreshBundledVehicleSaves.
    public static IReadOnlyList<VehicleSaveSource> BundledVehicleSaves => _bundledSaves ??= ScanBundledSaves();

    public static void RefreshBundledVehicleSaves() => _bundledSaves = null;

    // The save with this name, or null when neither a bundle nor the game's Vehicles folder has it.
    public static VehicleSaveSource? FindVehicleSave(string saveName)
    {
        foreach (VehicleSaveSource bundled in BundledVehicleSaves)
        {
            if (string.Equals(bundled.Name, saveName, StringComparison.Ordinal))
                return bundled;
        }
        foreach (VehicleSave save in VehicleSaves.AsSpan())
        {
            if (string.Equals(save.Id, saveName, StringComparison.Ordinal))
            {
                string folder = save is UncompressedVehicleSave uncompressed
                    ? uncompressed.Directory.FullName
                    : VehicleSaves.SaveFolderPath;
                return new VehicleSaveSource(save.Id, VehicleSaveOrigin.GameVehiclesFolder, folder, null, save);
            }
        }
        return null;
    }

    // Like FindVehicleSave, but a missing save throws with every place it looked.
    public static VehicleSaveSource ResolveVehicleSave(string saveName)
        => FindVehicleSave(saveName) ?? throw new InvalidOperationException(
            $"vehicle save '{saveName}' not found in the bundled vehicles ({DescribeBundles()}) " +
            $"or in the game's Vehicles folder ({VehicleSaves.SaveFolderPath}).");

    private static string DescribeBundles()
    {
        IReadOnlyList<VehicleSaveSource> saves = BundledVehicleSaves;
        if (saves.Count == 0)
            return "none";
        List<string> names = new List<string>(saves.Count);
        foreach (VehicleSaveSource save in saves)
            names.Add(save.Name);
        return string.Join(", ", names);
    }

    private static List<VehicleSaveSource> ScanBundledSaves()
    {
        List<VehicleSaveSource> found = new List<VehicleSaveSource>();
        string harnessName = typeof(VehicleSpawner).Assembly.GetName().Name ?? "HeadlessHarness";
        string? harnessDir = Path.GetDirectoryName(typeof(VehicleSpawner).Assembly.Location);
        if (!string.IsNullOrEmpty(harnessDir))
            AddBundle(found, Path.Combine(harnessDir, BundledVehiclesFolderName), VehicleSaveOrigin.HarnessBundle, harnessName);

        // The game builds this path from Constants.DocumentsFolderPath, which StarMap redirects to
        // the instance folder, so a run in an instance sees the consumers deployed there.
        ScanConsumerBundles(ModLibrary.LocalModsFolderPath, harnessDir, harnessName, found);

        if (found.Count > 0)
        {
            List<string> listed = new List<string>(found.Count);
            foreach (VehicleSaveSource save in found)
                listed.Add($"'{save.Name}' ({save.ModName})");
            HarnessLog.Line($"[harness] bundled vehicle saves: {string.Join(", ", listed)}.");
        }
        return found;
    }

    // A consumer is a mod folder whose mod.toml names the harness, the same marker HarnessRunner
    // uses to load consumer assemblies. Folders are visited in ordinal name order, so which bundle
    // wins a name clash does not depend on the file system.
    internal static void ScanConsumerBundles(string modsRoot, string? harnessDir, string harnessName, List<VehicleSaveSource> into)
    {
        string[] modDirs;
        try
        {
            if (!Directory.Exists(modsRoot))
                return;
            modDirs = Directory.GetDirectories(modsRoot);
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] could not list the mod folders under '{modsRoot}': {e.Message}");
            return;
        }
        Array.Sort(modDirs, StringComparer.Ordinal);

        string? harnessFull = harnessDir != null ? Path.GetFullPath(harnessDir).TrimEnd(Path.DirectorySeparatorChar) : null;
        foreach (string modDir in modDirs)
        {
            string full = Path.GetFullPath(modDir).TrimEnd(Path.DirectorySeparatorChar);
            if (harnessFull != null && string.Equals(full, harnessFull, StringComparison.OrdinalIgnoreCase))
                continue;
            string bundle = Path.Combine(modDir, BundledVehiclesFolderName);
            if (!Directory.Exists(bundle))
                continue;
            try
            {
                string toml = Path.Combine(modDir, "mod.toml");
                if (!File.Exists(toml) || !File.ReadAllText(toml).Contains(harnessName, StringComparison.Ordinal))
                    continue;
            }
            catch (Exception e)
            {
                HarnessLog.Line($"[harness] could not read the mod.toml in '{modDir}': {e.Message}; its vehicles are not used.");
                continue;
            }
            AddBundle(into, bundle, VehicleSaveOrigin.ConsumerBundle, Path.GetFileName(modDir));
        }
    }

    // Reads a save folder the way UncompressedVehicleSave.FromDirectory does: the name comes from
    // the meta.toml (SaveMetaData.FromDirectory), and a folder without a vehicle file is skipped.
    private static void AddBundle(List<VehicleSaveSource> into, string bundleDir, VehicleSaveOrigin origin, string modName)
    {
        string[] saveDirs;
        try
        {
            if (!Directory.Exists(bundleDir))
                return;
            saveDirs = Directory.GetDirectories(bundleDir);
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] could not list the bundled vehicles in '{bundleDir}': {e.Message}");
            return;
        }
        Array.Sort(saveDirs, StringComparer.Ordinal);

        foreach (string saveDir in saveDirs)
        {
            DirectoryInfo info = new DirectoryInfo(saveDir);
            string? name;
            try
            {
                name = SaveMetaData.FromDirectory(info)?.Name;
            }
            catch (Exception e)
            {
                HarnessLog.Line($"[harness] bundled vehicle '{saveDir}' has an unreadable meta.toml ({e.Message}); skipping it.");
                continue;
            }
            if (string.IsNullOrEmpty(name))
            {
                HarnessLog.Line($"[harness] bundled vehicle '{saveDir}' has no meta.toml with a name; skipping it.");
                continue;
            }
            if (!VehicleSaveData.ExistsIn(info))
            {
                HarnessLog.Line($"[harness] bundled vehicle '{name}' in '{saveDir}' has no {VehicleFileName}; skipping it.");
                continue;
            }

            VehicleSaveSource? earlier = null;
            foreach (VehicleSaveSource existing in into)
            {
                if (string.Equals(existing.Name, name, StringComparison.Ordinal))
                {
                    earlier = existing;
                    break;
                }
            }
            if (earlier != null)
            {
                HarnessLog.Line($"[harness] bundled vehicle '{name}' in '{saveDir}' is not used, because {earlier.Describe()} has a save of that name.");
                continue;
            }
            into.Add(new VehicleSaveSource(name, origin, info.FullName, modName, null));
        }
    }
}
