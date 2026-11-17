using System.Collections.Generic;
using System.Globalization;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Saves the whole running universe to XML and loads it back through Universe.DeserializeSave, the
// game's own load path, with the KSA_HEADLESS_VEHICLE save flying in it. Every vehicle must come back
// with the same save record (SaveRoundTrip.FirstDifference, so an angle a full turn apart counts as
// equal), with its orbit state vectors exactly at save precision and with a bit-identical mass. A
// second save must write the same XML, and the run's rules must be in force after the load although
// the loaded data names the opposite rules. The XML is written next to the run log, never into the
// game's save folder. Opt-in, because the load destroys and rebuilds every vehicle and resets the
// game's running part id, which changes what later tests see.
public sealed class SaveUniverseRoundTripTest : IHarnessTest
{
    private const int BurnSeconds = 3;
    private const int CoastSeconds = 2;

    public string Name => "save-universe-roundtrip";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool forceOffRails = PhysicsBubble._forceOffRails;
        double simulationSpeed = Universe.SimulationSpeed;
        bool radiation = Universe.Rules.Radiation;
        bool lifeSupport = Universe.Rules.LifeSupport;
        string? controlledId = Program.ControlledVehicle?.Id;
        bool ok = true;
        try
        {
            Vehicle? vehicle = ContentFixture.Spawn(session, Name, "HarnessUniverseRoundTrip", out int failure);
            if (vehicle == null)
                return failure;

            PhysicsBubble._forceOffRails = true;
            ContentFixture.Fly(vehicle, session.CreateDriver(), BurnSeconds, CoastSeconds);

            Dictionary<string, VehicleState> before = CaptureAll(system);
            string xml = SaveRoundTrip.SerializeUniverse();
            WriteNextToLog("universe-before.xml", xml);

            UniverseData data = SaveRoundTrip.ReadUniverse(xml);
            // The save names the opposite of the rules in force, the way a player's save made with
            // other settings would.
            data.Rules = new GameRules { Radiation = !Universe.Rules.Radiation, LifeSupport = !Universe.Rules.LifeSupport };
            string expectedXml = xml;
            SaveRoundTrip.LoadUniverse(data);

            if (!ReferenceEquals(Universe.CurrentSystem, system))
            {
                HarnessLog.Line($"[{Name}] FAIL: the load replaced the celestial system object.");
                return 1;
            }
            ok &= Compare(before, CaptureAll(system));
            ok &= CheckRules();

            string again = SaveRoundTrip.SerializeUniverse();
            string? difference = SaveRoundTrip.FirstDifference(expectedXml, again, out int fullTurns);
            if (difference != null)
            {
                WriteNextToLog("universe-after.xml", again);
                HarnessLog.Line($"[{Name}] FAIL: the universe saved after the load differs: {difference}");
                ok = false;
            }
            else
            {
                HarnessLog.Line($"[{Name}] universe XML after the load equals the saved one ({xml.Length} chars, " +
                                $"{fullTurns} angle(s) a full turn apart).");
            }

            HarnessLog.Line($"[{Name}] {TestSupport.Verdict(ok)} ({before.Count} vehicle(s) round-tripped).");
            return ok ? 0 : 1;
        }
        finally
        {
            PhysicsBubble._forceOffRails = forceOffRails;
            Universe.Rules.Radiation = radiation;
            Universe.Rules.LifeSupport = lifeSupport;
            Universe.SetSimulationSpeed(simulationSpeed, alert: false);
            TestSupport.DespawnNewVehicles(system, preexisting);
            Program.ControlledVehicle = controlledId != null ? system.Get(controlledId) as Vehicle : null;
        }
    }

    // The save record of a vehicle, its orbit state vectors (CCI position and velocity) and its total
    // mass. The attitude is left to the record, because the game saves it as XYZ angles and rebuilds
    // the quaternion from them on load, which is not bit-exact.
    private sealed record VehicleState(string Xml, double[] Orbit, double Mass);

    private static Dictionary<string, VehicleState> CaptureAll(CelestialSystem system)
    {
        Dictionary<string, VehicleState> states = new Dictionary<string, VehicleState>(StringComparer.Ordinal);
        for (int i = 0; i < system.Count; i++)
        {
            if (system.GetIndex(i) is not Vehicle { IsDisposed: false } vehicle)
                continue;
            StateVectors sv = vehicle.Orbit.StateVectors;
            double[] orbit = { sv.PositionCci.X, sv.PositionCci.Y, sv.PositionCci.Z, sv.VelocityCci.X, sv.VelocityCci.Y, sv.VelocityCci.Z };
            states[vehicle.Id] = new VehicleState(Fingerprint.SaveXml(vehicle.SerializeSave()), orbit, vehicle.TotalMass);
        }
        return states;
    }

    // The game writes every number of a save rounded to four decimals (NaNFilteringXmlWriter.WriteValue,
    // "0.####" after Math.Round away from zero), so a loaded orbit state is the saved one at that
    // precision. This formats and parses the value the same way, which makes the comparison exact.
    private static double AtSavePrecision(double value) =>
        double.Parse(Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private bool Compare(Dictionary<string, VehicleState> before, Dictionary<string, VehicleState> after)
    {
        bool ok = true;
        foreach (KeyValuePair<string, VehicleState> kv in before)
        {
            if (!after.TryGetValue(kv.Key, out VehicleState? loaded))
            {
                HarnessLog.Line($"[{Name}] FAIL: vehicle '{kv.Key}' did not come back from the load.");
                ok = false;
                continue;
            }
            string? difference = SaveRoundTrip.FirstDifference(kv.Value.Xml, loaded.Xml, out int fullTurns);
            bool orbit = true;
            for (int i = 0; i < kv.Value.Orbit.Length; i++)
                orbit &= SameBits(loaded.Orbit[i], AtSavePrecision(kv.Value.Orbit[i]));
            bool mass = SameBits(loaded.Mass, kv.Value.Mass);
            HarnessLog.Line($"[{Name}]   '{kv.Key}': save record {(difference == null ? "equal" : "DIFFERS")} " +
                            $"({fullTurns} angle(s) a full turn apart), orbit state {(orbit ? "exact at save precision" : "DIFFERS")}, " +
                            $"mass {(mass ? "bit-identical" : "DIFFERS")}");
            if (difference != null)
                HarnessLog.Line($"[{Name}]     first difference: {difference}");
            if (!orbit)
                HarnessLog.Line($"[{Name}]     orbit before {string.Join(", ", kv.Value.Orbit)}\n[{Name}]     orbit after  {string.Join(", ", loaded.Orbit)}");
            if (!mass)
                HarnessLog.Line($"[{Name}]     mass before {kv.Value.Mass:R}, after {loaded.Mass:R}");
            ok &= difference == null && orbit && mass;
        }
        foreach (string id in after.Keys)
        {
            if (!before.ContainsKey(id))
            {
                HarnessLog.Line($"[{Name}] FAIL: the load created vehicle '{id}', which was not saved.");
                ok = false;
            }
        }
        return ok;
    }

    private bool CheckRules()
    {
        GameRules requested = TestSupport.GetRunRules();
        GameRules rules = Universe.Rules;
        bool ok = rules.Radiation == requested.Radiation && rules.LifeSupport == requested.LifeSupport;
        HarnessLog.Line($"[{Name}] rules after the load: radiation={rules.Radiation}, life support={rules.LifeSupport} " +
                        $"(run asks for {requested.Radiation}/{requested.LifeSupport}) => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private void WriteNextToLog(string suffix, string text)
    {
        string dir = Path.GetDirectoryName(HarnessLog.FilePath) ?? HarnessLog.DataDirectory;
        string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(HarnessLog.FilePath)}-{suffix}");
        try
        {
            File.WriteAllText(path, text);
            HarnessLog.Line($"[{Name}] wrote {path}");
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[{Name}] could not write {path}: {e.Message}");
        }
    }
}
