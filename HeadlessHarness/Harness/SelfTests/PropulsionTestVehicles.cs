using KSA;

namespace HeadlessHarness.Harness;

// The save the propulsion and aerodynamics self-tests fly. Their assertions rely on its shape (a
// staged, axisymmetric liquid-fuel stack), so they name it directly instead of reading the flight
// test's KSA_HEADLESS_VEHICLE, and skip on a machine without it.
internal static class PropulsionTestVehicles
{
    public const string SaveId = "Test Vehicle 1";

    public static bool SaveExists()
        => VehicleSpawner.FindVehicleSave(SaveId) != null;

    // A circular orbit at the given altitude above the home body.
    public static Vehicle SpawnInOrbit(CelestialSystem system, IParentBody home, string id, double altitude)
    {
        Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + altitude, Universe.GetElapsedTime());
        return VehicleSpawner.SpawnFromSave(SaveId, system, home, id, orbit);
    }
}
