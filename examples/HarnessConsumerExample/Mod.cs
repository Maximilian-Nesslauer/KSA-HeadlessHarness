using StarMap.API;

namespace HarnessConsumerExample;

// Marks the assembly as a StarMap mod so the folder is a valid mod install (StarMap requires a
// [StarMapMod] class per mod assembly). No lifecycle hooks: this mod exists only to carry an
// IHarnessTest. HeadlessHarness loads this DLL itself after its bring-up and discovers the test,
// so the manifest order of the two mods does not matter.
[StarMapMod]
public sealed class Mod
{
}
