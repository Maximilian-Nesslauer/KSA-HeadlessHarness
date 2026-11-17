using System.Collections.Generic;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Gates the content load: any content error of a mod other than Core fails the run, with its kind,
// mod, file, tag and id. Core's own errors are the game's and are logged as information only, and so
// are the errors only a run without a renderer has (ContentCheck.HeadlessOnly: shaders, and the
// content that refers to them), in any mod. A content mod under test (a part mod the run enables next
// to Core) loads in the bring-up, so this test is what turns its broken XML into a failure instead of
// a log line. Every error goes to a CSV next to the run log.
public sealed class ContentLoadTest : IHarnessTest
{
    private const int InfoLogged = 10;

    public string Name => "content-load";

    public int Run(HeadlessSession session)
    {
        IReadOnlyList<ContentError> errors = ContentCheck.LoadErrors();
        HashSet<ContentError> headless = ContentCheck.HeadlessOnly(errors);
        List<ContentError> info = new List<ContentError>();
        List<ContentError> failures = new List<ContentError>();
        foreach (ContentError error in errors)
            (ContentCheck.IsCore(error) || headless.Contains(error) ? info : failures).Add(error);

        LogPartMods();
        HarnessData data = HarnessData.Create(Name, "kind,mod,file,tag,id,parent_element,detail,headless_only");
        foreach (ContentError error in errors)
            data.AppendRow(error.Kind, error.Mod, error.File, error.Tag, error.Id, error.ParentElement, error.Detail, headless.Contains(error));
        HarnessLog.Line($"[content-load] {errors.Count} content error(s) in the load: {info.Count} in {ContentCheck.CoreModId} " +
                        $"or only headless ({headless.Count} only headless), {failures.Count} in other mods.");

        for (int i = 0; i < info.Count && i < InfoLogged; i++)
            HarnessLog.Line($"[content-load]   info: {ContentCheck.Describe(info[i])}");
        if (info.Count > InfoLogged)
            HarnessLog.Line($"[content-load]   ... and {info.Count - InfoLogged} more, see the CSV.");

        foreach (ContentError error in failures)
        {
            string mod = string.IsNullOrEmpty(error.Mod) ? "(no mod)" : error.Mod;
            HarnessLog.Line($"[content-load]   FAIL {error.Kind} in mod '{mod}', file '{error.File}', tag '{error.Tag}', id '{error.Id}': " +
                            ContentCheck.Describe(error));
        }

        bool ok = failures.Count == 0;
        HarnessLog.Line($"[content-load] {TestSupport.Verdict(ok)} ({failures.Count} content error(s) outside {ContentCheck.CoreModId}).");
        return ok ? 0 : 1;
    }

    // Names the mods that brought part templates into the run, so the log shows which content mods
    // this gate covered.
    private static void LogPartMods()
    {
        SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (PartTemplate template in PartCheck.Templates(null))
        {
            string mod = template.Mod?.Id ?? "(no mod)";
            counts[mod] = counts.GetValueOrDefault(mod) + 1;
        }
        List<string> names = new List<string>();
        foreach (KeyValuePair<string, int> kv in counts)
            names.Add($"{kv.Key} ({kv.Value} part(s))");
        HarnessLog.Line($"[content-load] mods with parts besides {ContentCheck.CoreModId}: {(names.Count > 0 ? string.Join(", ", names) : "none")}.");
    }
}
