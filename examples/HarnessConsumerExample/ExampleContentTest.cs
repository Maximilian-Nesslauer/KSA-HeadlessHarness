using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HarnessConsumerExample;

// The gate a content mod (a part pack) puts in front of its release: no content error in its own
// files, and every one of its parts builds with mass, colliders and a closed aero mesh. A real part
// mod sets ModId to its own mod id and deploys next to the harness; this example names its own id,
// which brings no parts, so the part half reports that there is nothing to check.
public sealed class ExampleContentTest : IHarnessTest
{
    private const string ModId = "HarnessConsumerExample";

    public string Name => "example-content";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        HashSet<ContentError> headless = ContentCheck.HeadlessOnly(ContentCheck.LoadErrors());
        foreach (ContentError error in ContentCheck.ModErrors(new[] { ModId }))
        {
            // Shaders cannot load without a renderer; those errors say nothing about the mod.
            if (headless.Contains(error))
                continue;
            HarnessLog.Line($"[example-content] FAIL content error: {ContentCheck.Describe(error)}");
            ok = false;
        }

        IReadOnlyList<PartCheck.Result> parts = PartCheck.CheckMods(new[] { ModId });
        foreach (PartCheck.Result part in parts)
        {
            if (part.Passed)
                continue;
            HarnessLog.Line($"[example-content] FAIL part '{part.PartId}': {string.Join("; ", part.Problems)}");
            ok = false;
        }

        HarnessLog.Line($"[example-content] '{ModId}': {parts.Count} part(s) checked => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }
}
