using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Writes game state through the game's own save code and reads it back through its own load code,
// then compares what comes out. A field a module forgets to save, or reads back differently, shows
// up as the first difference between the two records, named by its element path.
//
// The game saves an orientation as XYZ angles in radians (TransformReference.RotationValue and the
// Body2Cce and Body2Phys of Vehicle.SerializeSave, through doubleQuat.ToXyzRadians) and builds a
// quaternion from them on load, so an angle of pi can come back as -pi. The comparison counts such a
// full turn as equal and reports how many it saw.
//
// The bubble-frame position, velocity, orientation and angular velocity are rebuilt from the saved
// AnalyticState by PhysicsStates.UpdateFromAnalytic and SetBodyRates during Vehicle.DeserializeSave.
// Its rounded inertial state remains compared; the redundant KinematicState fields are excluded
// because recomputing them can change their last saved decimal without losing persisted state.
public static class SaveRoundTrip
{
    // Equal: no difference in the compared state. FullTurnAngles: how many angle attributes
    // differed by a full turn. Module: the first part module whose save data differs, when the first
    // difference lies in one.
    public sealed record Result(bool Equal, string? FirstDifference, string? Module, int FullTurnAngles, string BeforeXml, string AfterXml);

    private static readonly HashSet<string> AngleElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "Rotation", "Body2Cce", "Body2Phys",
    };

    private const double FullTurnTolerance = 1e-4;

    private static readonly HashSet<string> RebuiltKinematics = new HashSet<string>(StringComparer.Ordinal)
    {
        "PositionPhys", "VelocityPhys", "Body2Phys", "BodyRates",
    };

    // Serializes the vehicle (Vehicle.SerializeSave), writes the record to XML and reads it back,
    // builds a second vehicle from it the way a save load does (CelestialSystem.DeserializeSave:
    // Vehicle.CreateVehicleFromSaveGameData, or KittenEva.CreateKittenFromSaveData for a kitten, then
    // the parent's child list), serializes that vehicle and compares the two XML records.
    //
    // The second vehicle needs an id of its own, because the celestial system registers astronomicals
    // by id. Its record is compared under the source id. It exists only for the comparison: it is
    // never stepped, its seats are cleared before it is destroyed (otherwise Universe.DestroyVehicle
    // would end the missions of the source's crew, whose kittens the copied seats name), and it is
    // destroyed before this returns.
    public static Result Vehicle(KSA.Vehicle vehicle)
    {
        CelestialSystem system = vehicle.System;
        List<VehicleData> context = system.SerializeSave().Vehicles;
        VehicleData before = context.Find(data => data.Id == vehicle.Id)
            ?? throw new InvalidOperationException($"Vehicle '{vehicle.Id}' was not serialized.");
        string beforeXml = Fingerprint.SaveXml(before);

        VehicleData loaded = Fingerprint.ReadSaveXml(beforeXml);
        string copyId = UniqueId(system, vehicle.Id + "-RoundTrip");
        loaded.Id = copyId;
        loaded.OnDataLoad(KSA.Mod.Empty);
        if (loaded.ParentBody.Get() is not IParentBody parent)
            throw new InvalidOperationException($"[HeadlessHarness] the parent '{loaded.ParentBody.Id}' of '{vehicle.Id}' is not an orbitable body.");

        KSA.Vehicle? copy = null;
        try
        {
            copy = string.IsNullOrEmpty(loaded.Character)
                ? KSA.Vehicle.CreateVehicleFromSaveGameData(system, loaded)
                : KittenEva.CreateKittenFromSaveData(system, loaded);
            parent.Children.Add(copy);
            // CelestialSystem.DeserializeSave restores targets in its second pass, after every
            // vehicle exists. The source's target already exists for this single-vehicle load.
            copy.SetTarget(vehicle.Target, vehicle.TargetPart);

            RestoreControl(copy, loaded);
            if (copy is KittenEva eva && loaded.OnLadder)
            {
                var restore = AccessTools.Method(typeof(KittenEva), "RestoreLadderAttach",
                    new[] { typeof(CelestialSystem), typeof(VehicleData), typeof(List<VehicleData>) })
                    ?? throw new InvalidOperationException("[HeadlessHarness] KittenEva.RestoreLadderAttach not found - game version may have changed.");
                restore.Invoke(eva, new object[] { system, loaded, context });
            }

            VehicleData after = system.SerializeSave().Vehicles.Find(data => data.Id == copy.Id)!;
            after.Id = vehicle.Id;
            string afterXml = Fingerprint.SaveXml(after);
            if (beforeXml == afterXml)
                return new Result(true, null, null, 0, beforeXml, afterXml);
            string? difference = FirstDifference(beforeXml, afterXml, out int fullTurns);
            return new Result(difference == null, difference, difference != null ? DifferingModule(before, after) : null,
                fullTurns, beforeXml, afterXml);
        }
        finally
        {
            if (copy != null)
            {
                foreach (IVASeat seat in copy.Parts.Modules.Get<IVASeat>())
                    seat.AssignedKittenHash = KeyHash.Zero;
                VehicleSpawner.Despawn(copy);
            }
        }
    }

    private static void RestoreControl(KSA.Vehicle vehicle, VehicleData data)
    {
        if (data.ControlPartId == 0 || data.RootPartInstance == null)
            return;
        PartTreeIterator live = new(vehicle.Parts.Root);
        PartInstanceTreeIterator saved = new(data.RootPartInstance);
        while (live.GetNextNode() is Part part && saved.GetNextNode() is PartInstance instance)
        {
            if (instance.LocalInstanceId != data.ControlPartId)
                continue;
            Part.Connector? connector = Array.Find(part.Connectors, c => c.Id == data.ControlConnectorId);
            vehicle.SetControlPart(part, connector);
            return;
        }
    }

    // The whole universe as the XML of a game save (UniverseData.Create written with
    // GameSaves.UniverseSerializer, as UniverseData.WriteTo writes universe.xml). The player's save
    // folder is not touched.
    public static string SerializeUniverse() => SerializeUniverse(UniverseData.Create());

    public static string SerializeUniverse(UniverseData data)
    {
        using MemoryStream stream = new MemoryStream();
        XmlHelper.SerializeWithoutNaN(GameSaves.UniverseSerializer, data, stream, indent: true);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    // Reads universe XML the way UniverseData.LoadFrom does (GameSaves.UniverseSerializer, then
    // UniverseData.OnDataLoad with Mod.Empty).
    public static UniverseData ReadUniverse(string xml)
    {
        using StringReader reader = new StringReader(xml);
        UniverseData data = GameSaves.UniverseSerializer.Deserialize(reader) as UniverseData
            ?? throw new InvalidOperationException("[HeadlessHarness] the universe XML did not read back as UniverseData.");
        data.OnDataLoad(KSA.Mod.Empty);
        return data;
    }

    // Loads universe data into the running game through Universe.DeserializeSave, as the load menu
    // does: every vehicle is destroyed and the saved ones are built again, the sim clock, kitten
    // roster and camera are restored, and the simulation speed is set to 1. The harness applies the
    // run's rules again after the load (see HeadlessSession.Content). A SimDriver made before the load
    // keeps its own clock, so create a new one with HeadlessSession.CreateDriver afterwards.
    public static void LoadUniverse(UniverseData data) => Universe.DeserializeSave(data);

    // The path of the first element, attribute or text that differs between two XML documents, such
    // as "/VehicleData/RootPartRef[0]/PartRef[1]/Components[0]/TankState[0]@Mass: '12.5' != '12.25'".
    // Siblings are indexed per element name. Null when the documents are equal, counting an angle
    // that differs by a full turn as equal.
    public static string? FirstDifference(string beforeXml, string afterXml) =>
        FirstDifference(beforeXml, afterXml, out _);

    public static string? FirstDifference(string beforeXml, string afterXml, out int fullTurnAngles)
    {
        XElement a = XDocument.Parse(beforeXml).Root!;
        XElement b = XDocument.Parse(afterXml).Root!;
        fullTurnAngles = 0;
        return Compare(a, b, "/" + a.Name.LocalName, ref fullTurnAngles);
    }

    private static string? Compare(XElement a, XElement b, string path, ref int fullTurns)
    {
        if (a.Name != b.Name)
            return $"{path}: element <{a.Name.LocalName}> != <{b.Name.LocalName}>";

        bool angles = AngleElements.Contains(a.Name.LocalName);
        foreach (XAttribute attribute in a.Attributes())
        {
            string? other = b.Attribute(attribute.Name)?.Value;
            if (other == attribute.Value)
                continue;
            if (angles && other != null && IsFullTurn(attribute.Value, other))
            {
                fullTurns++;
                continue;
            }
            return $"{path}@{attribute.Name.LocalName}: '{attribute.Value}' != '{other ?? "(missing)"}'";
        }
        foreach (XAttribute attribute in b.Attributes())
        {
            if (a.Attribute(attribute.Name) == null)
                return $"{path}@{attribute.Name.LocalName}: '(missing)' != '{attribute.Value}'";
        }

        List<XElement> childrenA = ComparedChildren(a);
        List<XElement> childrenB = ComparedChildren(b);
        if (childrenA.Count == 0 && childrenB.Count == 0)
            return a.Value == b.Value ? null : $"{path}: '{a.Value}' != '{b.Value}'";

        Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
        int count = Math.Min(childrenA.Count, childrenB.Count);
        for (int i = 0; i < count; i++)
        {
            string name = childrenA[i].Name.LocalName;
            int n = index.GetValueOrDefault(name);
            index[name] = n + 1;
            string? difference = Compare(childrenA[i], childrenB[i], $"{path}/{name}[{n}]", ref fullTurns);
            if (difference != null)
                return difference;
        }
        if (childrenA.Count != childrenB.Count)
        {
            XElement extra = childrenA.Count > childrenB.Count ? childrenA[count] : childrenB[count];
            return $"{path}: {childrenA.Count} child element(s) != {childrenB.Count}, first unmatched <{extra.Name.LocalName}>";
        }
        return null;
    }

    private static List<XElement> ComparedChildren(XElement element)
    {
        List<XElement> children = new List<XElement>(element.Elements());
        if (element.Name.LocalName == "KinematicState")
            children.RemoveAll(child => RebuiltKinematics.Contains(child.Name.LocalName));
        return children;
    }

    private static bool IsFullTurn(string a, string b)
    {
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return false;
        return Math.Abs(Math.Abs(x - y) - 2.0 * Math.PI) <= FullTurnTolerance;
    }

    // Walks both part trees in the same order and names the first module whose save data differs,
    // compared field by field with the game's own SaveDataEquality.AreEqual (bit-exact for floating
    // point values). Null when every module compares equal, which means the difference is outside
    // the part modules.
    private static string? DifferingModule(VehicleData before, VehicleData after)
    {
        List<PartInstance> partsA = new List<PartInstance>();
        List<PartInstance> partsB = new List<PartInstance>();
        Collect(before.RootPartInstance, partsA);
        Collect(after.RootPartInstance, partsB);
        int count = Math.Min(partsA.Count, partsB.Count);
        for (int i = 0; i < count; i++)
        {
            List<ModuleBase.SaveDataBase> a = partsA[i].Components;
            List<ModuleBase.SaveDataBase> b = partsB[i].Components;
            int modules = Math.Min(a.Count, b.Count);
            for (int m = 0; m < modules; m++)
            {
                if (!SaveDataEquality.AreEqual(a[m], b[m]))
                    return $"part '{partsA[i].InstanceOf}' #{partsA[i].LocalInstanceId}, module {a[m].GetType().Name} [{m}]";
            }
            if (a.Count != b.Count)
                return $"part '{partsA[i].InstanceOf}' #{partsA[i].LocalInstanceId}, {a.Count} module(s) != {b.Count}";
        }
        return partsA.Count != partsB.Count ? $"{partsA.Count} part(s) != {partsB.Count}" : null;
    }

    private static void Collect(PartInstance? part, List<PartInstance> into)
    {
        if (part == null)
            return;
        into.Add(part);
        if (part.SubPartInstances != null)
        {
            foreach (PartInstance sub in part.SubPartInstances)
                Collect(sub, into);
        }
        if (part.Children != null)
        {
            foreach (PartInstance child in part.Children)
                Collect(child, into);
        }
    }

    private static string UniqueId(CelestialSystem system, string baseId)
    {
        string id = baseId;
        for (int n = 2; system.Get(id) != null; n++)
            id = $"{baseId}{n}";
        return id;
    }

    internal static void LogXmlPair(string tag, Result result)
    {
        try
        {
            string dir = Path.GetDirectoryName(HarnessLog.FilePath) ?? HarnessLog.DataDirectory;
            string baseName = Path.GetFileNameWithoutExtension(HarnessLog.FilePath);
            string before = Path.Combine(dir, $"{baseName}-{tag}-before.xml");
            string after = Path.Combine(dir, $"{baseName}-{tag}-after.xml");
            File.WriteAllText(before, result.BeforeXml);
            File.WriteAllText(after, result.AfterXml);
            HarnessLog.Line($"[harness] save records written: {before} and {after}");
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] could not write the save records: {e.Message}");
        }
    }
}
