using Brutal.Logging;
using HeadlessHarness.Harness;
using KSA;
using StarMap.API;

namespace HeadlessHarness;

// StarMap fires [StarMapBeforeMain] during StarMapCore.Init, before it invokes KSA Program.Main,
// so this runs before any GLFW window or Vulkan renderer is created. The harness does its GPU-free
// bring-up and sim work here, then exits the process so the normal (GPU) game never starts.
[StarMapMod]
public sealed class Mod
{
    private const string TestedGameVersion = "v2026.7.6.4939";

    [StarMapBeforeMain]
    public void OnBeforeMain()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[HeadlessHarness] BeforeMain. Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[HeadlessHarness] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Bring-up may need re-verification against the decompiled source.");

        // Only take over when explicitly asked, so an installed harness never hijacks a normal
        // launch. When set, run the harness GPU-free and exit before KSA Program.Main (the GPU game).
        // Optional: KSA_HEADLESS_VEHICLE names a Vehicles-folder save for the flight test,
        // KSA_HEADLESS_TESTS filters which discovered tests run (comma-separated names), and
        // KSA_HEADLESS_LOG overrides the per-run log file path.
        if (Environment.GetEnvironmentVariable("KSA_HEADLESS_HARNESS") == "1")
        {
            int exitCode = HarnessMain.Run();
            DefaultCategory.Log.Info($"[HeadlessHarness] Done, exit code {exitCode}. Exiting before game start.");
            Environment.Exit(exitCode);
        }

        DefaultCategory.Log.Info("[HeadlessHarness] Idle (set KSA_HEADLESS_HARNESS=1 to run the harness).");
    }
}
