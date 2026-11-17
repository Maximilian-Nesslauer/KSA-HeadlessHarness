using StarMap.API;

namespace HarnessConsumerExample;

// Marks the assembly as a StarMap mod so the folder is a valid mod install (StarMap requires a
// [StarMapMod] class per mod assembly). HeadlessHarness loads this DLL itself after its bring-up and
// discovers the tests, so the manifest order of the two mods does not matter.
//
// The hooks stand in for those of a real mod, which applies its Harmony patches in them. A harness
// run never reaches the point where StarMap calls them, so a test calls them through HarnessMods;
// ExampleModLifecycleTest counts the calls here to show it.
[StarMapMod]
public sealed class Mod
{
    public static int ImmediateLoadCalls { get; private set; }

    public static int AllModsLoadedCalls { get; private set; }

    public static int UnloadCalls { get; private set; }

    [StarMapImmediateLoad]
    public void OnImmediateLoad(KSA.Mod definingMod) => ImmediateLoadCalls++;

    [StarMapAllModsLoaded]
    public void OnAllModsLoaded() => AllModsLoadedCalls++;

    [StarMapUnload]
    public void OnUnload() => UnloadCalls++;
}
