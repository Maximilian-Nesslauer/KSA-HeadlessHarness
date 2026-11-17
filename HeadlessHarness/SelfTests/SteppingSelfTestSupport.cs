using Brutal.Numerics;
using HarmonyLib;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Shared set-up of the stepping, accessor, stand-in and kFS self-tests.
internal static class SteppingSelfTestSupport
{
    private static Func<double3, doubleQuat, double3>? _rotate;

    // The game's Double3Ex.Transform(double3, doubleQuat). It is bound by reflection because the
    // compiler resolves the Transform overloads at the call site, and one of them takes a
    // BepuUtilities type that the harness does not reference.
    public static double3 Rotate(double3 value, doubleQuat rotation)
    {
        _rotate ??= AccessTools.Method(typeof(Double3Ex), "Transform", new[] { typeof(double3), typeof(doubleQuat) })
                        ?.CreateDelegate<Func<double3, doubleQuat, double3>>()
                    ?? throw new InvalidOperationException(
                        "[HeadlessHarness] Double3Ex.Transform(double3, doubleQuat) not found - game version may have changed.");
        return _rotate(value, rotation);
    }

    // The vehicle save the run names for flight tests, or null with a SKIP line when it is unset or
    // missing from the Vehicles folder.
    public static string? ResolveFlightSave(string testName)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrWhiteSpace(saveId))
        {
            HarnessLog.Line($"[{testName}] SKIP: {TestSupport.VehicleEnvVar} is not set (name a save in the game's Vehicles folder).");
            return null;
        }
        foreach (VehicleSave save in VehicleSaves.AsSpan())
        {
            if (string.Equals(save.Id, saveId, StringComparison.Ordinal))
                return saveId;
        }
        HarnessLog.Line($"[{testName}] SKIP: vehicle save '{saveId}' is not in {VehicleSaves.SaveFolderPath}.");
        return null;
    }

    // The kFS tick grid and every step of these tests lie on the 10 ms lattice of
    // KSA.Kfs.Host.TickGrid. A cursor that an earlier test left between two lattice points is moved
    // to the next one, so that two runs of a test start in the same phase.
    public static void AlignToLattice(SimDriver driver)
    {
        const long latticeNs = 10_000_000L;
        long offset = (long)(driver.Elapsed.Nanoseconds % latticeNs);
        if (offset < 0)
            offset += latticeNs;
        if (offset != 0)
            driver.Step((latticeNs - offset) / 1e9);
    }
}
