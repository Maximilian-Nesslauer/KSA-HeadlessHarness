using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Brutal.Numerics;
using KSA;

namespace HeadlessHarness.Harness;

// A compact identity of a vehicle's complete state, for bit-exact comparisons across runs or across a
// save round trip. XmlSha256 hashes the XML of Vehicle.SerializeSave, the record the game writes for
// the vehicle into a save: part tree, module states (tank contents, engine and seat states), sequence
// state, flight computer, orbit and kinematic state. StateBits holds the bit patterns of the live
// orbit state vectors, attitude, body rates and total mass, which the XML holds only in the form the
// save writes them (every number rounded to four decimals, the attitude as XYZ angles).
//
// The record names the kitten in each seat, and the game draws kitten names from Random.Shared
// (KittenNameGenerator.Generate, through KittenRosterData), so the same flight seats differently
// named crew in every process. The fingerprint writes an occupied seat as "(crew)" instead of the
// name, which keeps whether a seat is taken and drops who sits in it.
public sealed record Fingerprint(string XmlSha256, string StateBits)
{
    // What an occupied seat reads in the fingerprinted record.
    public const string CrewPlaceholder = "(crew)";

    private static XmlSerializer? _vehicleSerializer;

    // XmlSha256 and StateBits joined, for one-line storage and comparison.
    public string Signature => $"{XmlSha256};{StateBits}";

    public override string ToString() => Signature;

    public static Fingerprint Of(Vehicle vehicle) => Of(vehicle, out _);

    // saveXml is the hashed record, for a caller that keeps it to name the first difference later
    // (SaveRoundTrip.FirstDifference).
    public static Fingerprint Of(Vehicle vehicle, out string saveXml)
    {
        VehicleData data = vehicle.System.SerializeSave().Vehicles.Find(data => data.Id == vehicle.Id)
            ?? throw new InvalidOperationException($"Vehicle '{vehicle.Id}' was not serialized.");
        MaskCrewNames(data.RootPartInstance);
        string xml = SaveXml(data);
        saveXml = xml;
        StateVectors state = vehicle.Orbit.StateVectors;
        doubleQuat attitude = vehicle.Body2Cce;
        double3 rates = vehicle.BodyRates;
        string bits = string.Join(",", new[]
        {
            Bits(state.PositionCci.X), Bits(state.PositionCci.Y), Bits(state.PositionCci.Z),
            Bits(state.VelocityCci.X), Bits(state.VelocityCci.Y), Bits(state.VelocityCci.Z),
            Bits(attitude.X), Bits(attitude.Y), Bits(attitude.Z), Bits(attitude.W),
            Bits(rates.X), Bits(rates.Y), Bits(rates.Z),
            Bits(vehicle.TotalMass),
        });
        return new Fingerprint(Sha256(xml), bits);
    }

    private static void MaskCrewNames(PartInstance? part)
    {
        if (part == null)
            return;
        foreach (ModuleBase.SaveDataBase component in part.Components)
        {
            if (component is IVASeat.SaveData seat && !string.IsNullOrEmpty(seat.AssignedKittenName))
                seat.AssignedKittenName = CrewPlaceholder;
        }
        if (part.SubPartInstances != null)
        {
            foreach (PartInstance sub in part.SubPartInstances)
                MaskCrewNames(sub);
        }
        if (part.Children != null)
        {
            foreach (PartInstance child in part.Children)
                MaskCrewNames(child);
        }
    }

    // The XML of one vehicle record, written the way the game writes a save
    // (XmlHelper.SerializeWithoutNaN with XmlHelper.AttributeOverrides, which lists every module
    // save data type). The game serializes VehicleData only as part of a UniverseData, so the harness
    // builds its own serializer for the type, once per process.
    public static string SaveXml(VehicleData data)
    {
        using MemoryStream stream = new MemoryStream();
        XmlHelper.SerializeWithoutNaN(VehicleSerializer, data, stream, indent: true);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    // Reads a vehicle record written by SaveXml and resolves its ids the way the game does after it
    // reads a save (VehicleData.OnDataLoad with Mod.Empty, as UniverseData.LoadFrom does).
    public static VehicleData ReadSaveXml(string xml)
    {
        using StringReader reader = new StringReader(xml);
        VehicleData data = VehicleSerializer.Deserialize(reader) as VehicleData
            ?? throw new InvalidOperationException("[HeadlessHarness] the vehicle XML did not read back as VehicleData.");
        data.OnDataLoad(KSA.Mod.Empty);
        return data;
    }

    // SHA-256 of the UTF-8 bytes, as lowercase hex.
    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static XmlSerializer VehicleSerializer =>
        _vehicleSerializer ??= new XmlSerializer(typeof(VehicleData), XmlHelper.AttributeOverrides);

    private static string Bits(double value) =>
        BitConverter.DoubleToInt64Bits(value).ToString(CultureInfo.InvariantCulture);
}
