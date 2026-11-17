using HeadlessHarness.Core;
using HeadlessHarness.Harness;

namespace HarnessConsumerExample;

// The worked example of a test that needs its mod's StarMap hooks to have run, as a mod that
// applies its Harmony patches in [StarMapAllModsLoaded] does. HarnessMods calls them by mod id, and
// the calls nest: only the first load and the last unload reach the hooks, so each test can load in
// a try and unload in its finally without knowing what other tests did.
public sealed class ExampleModLifecycleTest : IHarnessTest
{
    private const string ModId = "HarnessConsumerExample";

    public string Name => "example-mod-lifecycle";

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        int immediate = Mod.ImmediateLoadCalls;
        int allLoaded = Mod.AllModsLoadedCalls;
        int unload = Mod.UnloadCalls;

        HarnessMods.InvokeAllModsLoaded(ModId);
        try
        {
            t.Check("the load hooks ran once", Mod.ImmediateLoadCalls == immediate + 1 && Mod.AllModsLoadedCalls == allLoaded + 1);

            HarnessMods.InvokeAllModsLoaded(ModId);
            try
            {
                t.Check("a nested load calls no hook", Mod.ImmediateLoadCalls == immediate + 1 && Mod.AllModsLoadedCalls == allLoaded + 1);
            }
            finally
            {
                HarnessMods.Unload(ModId);
            }
            t.Check("the nested unload keeps the mod loaded", HarnessMods.IsLoaded(ModId) && Mod.UnloadCalls == unload);
        }
        finally
        {
            HarnessMods.Unload(ModId);
        }
        t.Check("the last unload calls the unload hook once", !HarnessMods.IsLoaded(ModId) && Mod.UnloadCalls == unload + 1);

        HarnessMods.Unload(ModId);
        t.Check("an unload without a load does nothing", Mod.UnloadCalls == unload + 1);
        return t.Finish();
    }
}
