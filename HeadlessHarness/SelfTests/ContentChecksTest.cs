using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

public sealed class ContentChecksTest : IHarnessTest
{
    public string Name => "content-checks";

    public int Run(HeadlessSession session)
    {
        ContentError shader = Error(ContentErrorKind.LoadFailed, "Shader", "Shared",
            "Load threw NullReferenceException: Renderer is null compiling shader 'test.vert'");
        ContentError material = Error(ContentErrorKind.FailedRef, "PbrMaterial", "Material1",
            "ShaderReference \"Shared\" failed to load");
        string materialType = ModLibrary.TagTable.TryGet("PbrMaterial", out ContentTagBinding binding)
            ? binding.Type.Name : throw new InvalidOperationException("The PbrMaterial content tag is missing.");
        ContentError consumer = Error(ContentErrorKind.FailedRef, "Part", "Part1",
            $"{materialType} \"Material1\" failed to load");
        ContentError unrelated = Error(ContentErrorKind.FailedRef, "Part", "Part2",
            "PartTemplate \"Shared\" failed to load");
        ContentError notShader = Error(ContentErrorKind.LoadFailed, "Part", "Part3", shader.Detail);
        ContentError missingFile = Error(ContentErrorKind.LoadFailed, "Shader", "Missing",
            "Load threw NullReferenceException: Shader file does not exist in 'missing.vert'");
        ContentError invalidValue = Error(ContentErrorKind.InvalidValue, "Part", "Part1", "Invalid mass.");
        ContentError[] errors = { consumer, unrelated, material, notShader, missingFile, invalidValue, shader };
        HashSet<ContentError> headless = ContentCheck.HeadlessOnly(errors);
        bool classified = headless.SetEquals(new[] { shader, material, consumer });

        const string before = "<VehicleData><AnalyticState><Body2Cce X='3.1416'/><PositionCci X='1'/><BodyRates X='0.25'/></AnalyticState>"
            + "<KinematicState><PositionPhys X='4'/><VelocityPhys X='5'/><Body2Phys X='2.4'/><BodyRates X='0.1234'/></KinematicState>"
            + "<Components><TankState Mass='12.5'/></Components></VehicleData>";
        string equivalent = before.Replace("3.1416", "-3.1416").Replace("X='4'", "X='40'").Replace("X='5'", "X='50'");
        equivalent = equivalent.Replace("X='2.4'", "X='2.4001'").Replace("X='0.1234'", "X='0.1235'");
        bool fullTurn = SaveRoundTrip.FirstDifference(before, equivalent, out int turns) == null && turns == 1;
        string? difference = SaveRoundTrip.FirstDifference(before, before.Replace("12.5", "12.25"));
        bool named = difference != null && difference.Contains("/Components[0]/TankState[0]@Mass", StringComparison.Ordinal);
        bool orbit = SaveRoundTrip.FirstDifference(before, before.Replace("X='1'", "X='2'")) != null;
        bool angle = SaveRoundTrip.FirstDifference(before, before.Replace("3.1416", "3.1415")) != null;
        bool rates = SaveRoundTrip.FirstDifference(before, before.Replace("X='0.25'", "X='0.3'")) != null;
        bool structure = SaveRoundTrip.FirstDifference(before, before.Replace("<TankState Mass='12.5'/>", "")) != null;
        bool ok = classified && fullTurn && named && orbit && angle && rates && structure;
        HarnessLog.Line($"[{Name}] typed shader dependencies={classified}, full-turn angles={fullTurn}, "
            + $"module path={named}, orbit changes={orbit}, angle changes={angle}, missing modules={structure} => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static ContentError Error(ContentErrorKind kind, string tag, string id, string detail) =>
        new ContentError(kind, "ContentChecks", "test.xml", tag, id, string.Empty, detail,
            Array.Empty<string>(), 0, 0, 0, 0);
}
