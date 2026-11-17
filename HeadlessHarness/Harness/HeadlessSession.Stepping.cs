using System.Threading;
using HarmonyLib;
using KSA;

namespace HeadlessHarness.Harness;

// Render stand-ins on the vehicle solver path, and the kFS set-up of the run.
//
// Universe.ApplyVehicleSolvers hands each vehicle's render events to the main thread
// (PhysicsBubble.ApplyRenderEventsToVehicles). A touchdown on terrain raises a GroundImpactEvent and
// an entry into the sea a WaterSplashEvent, whose Apply calls Celestial.TrySpawnGroundImpact and
// Celestial.TrySpawnWaterSplash. Both start particle emitters through Program.Instance.ParticleSystem,
// which is null headless, so the first landing of any test would throw out of the step. They only
// spawn particles, so they are skipped. docs/stepping.md lists the other render events and why they
// need no stand-in.
public sealed partial class HeadlessSession
{
    private static int _skippedGroundImpacts;
    private static int _skippedWaterSplashes;

    // How often the solver raised a ground impact or a water splash that the stand-ins skipped. A test
    // reads the difference across its drop to prove the event fired.
    public static int SkippedGroundImpactCount => Volatile.Read(ref _skippedGroundImpacts);

    public static int SkippedWaterSplashCount => Volatile.Read(ref _skippedWaterSplashes);

    static partial void InstallSteppingPatches(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(Celestial), nameof(Celestial.TrySpawnGroundImpact), new[] { typeof(GroundImpactEvent) })
                ?? throw Drift("Celestial.TrySpawnGroundImpact(GroundImpactEvent)"),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(SkipGroundImpact)));
        harmony.Patch(
            AccessTools.Method(typeof(Celestial), nameof(Celestial.TrySpawnWaterSplash), new[] { typeof(WaterSplashEvent) })
                ?? throw Drift("Celestial.TrySpawnWaterSplash(WaterSplashEvent)"),
            prefix: new HarmonyMethod(typeof(HeadlessSession), nameof(SkipWaterSplash)));

        // Not a patch: the kFS switch and output have to be set before the first step, and this is the
        // feature hook that runs at the start of BringUp.
        Kfs.ConfigureForRun();
    }

    private static bool SkipGroundImpact()
    {
        Interlocked.Increment(ref _skippedGroundImpacts);
        return false;
    }

    private static bool SkipWaterSplash()
    {
        Interlocked.Increment(ref _skippedWaterSplashes);
        return false;
    }
}
