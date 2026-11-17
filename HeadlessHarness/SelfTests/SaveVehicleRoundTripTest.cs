using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Flies the KSA_HEADLESS_VEHICLE save for a few seconds, then saves it, loads the record into a new
// vehicle the way a game load does and saves that one again (SaveRoundTrip.Vehicle). The two records
// must be equal. A difference names the first element path and the module whose save data changed,
// and both records are written next to the run log.
public sealed class SaveVehicleRoundTripTest : IHarnessTest
{
    private const int BurnSeconds = 3;
    private const int CoastSeconds = 2;

    public string Name => "save-vehicle-roundtrip";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool forceOffRails = PhysicsBubble._forceOffRails;
        try
        {
            Vehicle? vehicle = ContentFixture.Spawn(session, Name, "HarnessSaveRoundTrip", out int failure);
            if (vehicle == null)
                return failure;

            PhysicsBubble._forceOffRails = true;
            ContentFixture.Fly(vehicle, session.CreateDriver(), BurnSeconds, CoastSeconds);
            vehicle.SetTarget(vehicle.Parent as IOrbiter);
            Part control = vehicle.Parts.Modules.Get<Control>()[0].Parent.FullPart;
            vehicle.SetControlPart(control);
            string sourceXml = Fingerprint.SaveXml(vehicle.SerializeSave());
            HashSet<string> beforeCopy = TestSupport.CollectVehicleIds(system);

            SaveRoundTrip.Result result = SaveRoundTrip.Vehicle(vehicle);
            VehicleData record = Fingerprint.ReadSaveXml(result.BeforeXml);
            bool controlSaved = record.ControlPartId != 0
                && record.ControlConnectorId == (vehicle.ControlConnector?.Id ?? string.Empty);
            bool isolated = beforeCopy.SetEquals(TestSupport.CollectVehicleIds(system))
                && sourceXml == Fingerprint.SaveXml(vehicle.SerializeSave());
            if (!isolated)
                HarnessLog.Line($"[{Name}] FAIL: the round trip changed the source record or left an extra vehicle.");
            if (!result.Equal)
            {
                SaveRoundTrip.LogXmlPair(Name, result);
                HarnessLog.Line($"[{Name}] first difference: {result.FirstDifference}");
                HarnessLog.Line($"[{Name}] module: {result.Module ?? "none, the difference is outside the part modules"}");
            }
            HarnessLog.Line($"[{Name}] '{vehicle.Id}': {vehicle.Parts.Parts.Length} part(s), record {result.BeforeXml.Length} chars, " +
                            $"{result.FullTurnAngles} angle(s) back a full turn apart => {TestSupport.Verdict(result.Equal)}");
            if (!controlSaved)
                HarnessLog.Line($"[{Name}] FAIL: the saved record omitted the selected control reference.");
            return result.Equal && isolated && controlSaved ? 0 : 1;
        }
        finally
        {
            PhysicsBubble._forceOffRails = forceOffRails;
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }
}
