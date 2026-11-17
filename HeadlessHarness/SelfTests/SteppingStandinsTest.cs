using System.Collections.Generic;
using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Drops the flight-test vehicle onto dry ground and into the sea of the home body, so that the solver
// raises a GroundImpactEvent and a WaterSplashEvent and the main thread applies them. Without the
// render stand-ins (HeadlessSession.Stepping.cs) the first of them throws out of the step. Passes when
// both events reached a stand-in and the steps ran on without an exception; the drop may break parts,
// which exercises the part failure and destruction events too. Opt-in, because a contact simulation
// near the surface is slow.
public sealed class SteppingStandinsTest : IHarnessTest
{
    private const double DropSpeedMps = 5.0;
    private const double ClearanceM = 10.0;
    private const double Dt = 0.02;
    private const double MaxFallSeconds = 15.0;
    private const double SettleSeconds = 1.0;
    // A site is dry or wet only with this margin to the sea level, so the vehicle cannot touch the
    // other surface on the way down.
    private const double SiteMarginM = 200.0;

    public string Name => "standins";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        string? saveId = SteppingSelfTestSupport.ResolveFlightSave(Name);
        if (saveId == null)
            return 0;
        CelestialSystem system = session.System;
        if (system.HomeBody is not Celestial home)
        {
            HarnessLog.Line("[standins] SKIP: the home body of the loaded system is no celestial with a surface.");
            return 0;
        }

        SimDriver driver = session.CreateDriver();
        // The surface turns with the body, so the bodies move as in the running game.
        driver.StepOrbits = true;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool ok = true;
        try
        {
            double radius = MeasureBoundingRadius(saveId, system, home, driver);
            double? seaLevel = home.BodyTemplate.OceanReference is { } ocean ? home.MeanRadius + ocean.Level.InMeters() : null;

            if (FindSite(home, seaLevel, wet: false) is { } land)
            {
                ok &= Drop(saveId, system, home, driver, land.DirCcf, land.SurfaceRadius, radius, "ground",
                    () => HeadlessSession.SkippedGroundImpactCount);
            }
            else
            {
                HarnessLog.Line($"[standins] FAIL: no dry site found on '{home.Id}'.");
                ok = false;
            }

            if (seaLevel == null)
                HarnessLog.Line($"[standins] '{home.Id}' has no ocean; the water splash is not tried.");
            else if (FindSite(home, seaLevel, wet: true) is { } sea)
                ok &= Drop(saveId, system, home, driver, sea.DirCcf, seaLevel.Value, radius, "sea",
                    () => HeadlessSession.SkippedWaterSplashCount);
            else
                HarnessLog.Line($"[standins] '{home.Id}' has an ocean but no site deeper than {SiteMarginM:F0}m; the water splash is not tried.");
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        HarnessLog.Line($"[standins] => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    // The spawn keeps the attitude of the save, so the vehicle may land on any side: its centre of mass
    // starts one bounding sphere radius and the clearance above the surface.
    private static double MeasureBoundingRadius(string saveId, CelestialSystem system, Celestial home, SimDriver driver)
    {
        Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + 500_000.0, driver.Elapsed);
        Vehicle probe = VehicleSpawner.SpawnFromSave(saveId, system, home, "HarnessStandinsSize", orbit);
        double radius = probe.BoundingSphereRadiusBody;
        VehicleSpawner.Despawn(probe);
        return radius;
    }

    private static bool Drop(string saveId, CelestialSystem system, Celestial home, SimDriver driver,
        double3 dirCcf, double surfaceRadius, double boundingRadius, string label, Func<int> eventCount)
    {
        UniverseTime now = driver.Elapsed;
        double3 upCci = SteppingSelfTestSupport.Rotate(dirCcf, home.GetCcf2Cci(now));
        double3 positionCci = upCci * (surfaceRadius + boundingRadius + ClearanceM);
        double3 velocityCci = double3.Cross(((IParentBody)home).GetAngularVelocityCci(), positionCci) - upCci * DropSpeedMps;
        Orbit orbit = Orbit.CreateFromStateCci(home, now, positionCci, velocityCci, VehicleSpawner.OrbitLineColor);

        int before = eventCount();
        Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "HarnessStandins_" + label, orbit);
        TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
        bool fired = driver.StepUntil(() => eventCount() > before, Dt, MaxFallSeconds,
            () => $"no {label} event; altitude {vehicle.GetPositionCci().Length() - surfaceRadius:F2}m above the surface");
        // The steps after the event apply whatever the touchdown raised next: part failures, a
        // destruction, more contacts.
        driver.StepFor(SettleSeconds, Dt);
        string state = vehicle.IsDisposed ? "destroyed" : vehicle.Situation.ToString();
        HarnessLog.Line($"[standins] {label} drop from {boundingRadius + ClearanceM:F1}m at {DropSpeedMps:F1}m/s: " +
                        $"{eventCount() - before} event(s) taken by the stand-in, vehicle {state} => {TestSupport.Verdict(fired)}");
        return fired;
    }

    // The first site of a fixed latitude and longitude grid that is dry land (or deep sea) by the margin.
    private static (double3 DirCcf, double SurfaceRadius)? FindSite(Celestial home, double? seaLevel, bool wet)
    {
        for (int lat = -60; lat <= 60; lat += 10)
        {
            for (int lon = -180; lon < 180; lon += 10)
            {
                double3 dirCcf = home.GetDirCcfFromLatLon(lat, lon);
                double terrain = home.MeanRadius + home.GetTerrainHeightFromDirCcf(dirCcf);
                bool dry = seaLevel == null || terrain > seaLevel.Value + SiteMarginM;
                bool deep = seaLevel != null && terrain < seaLevel.Value - SiteMarginM;
                if (wet ? deep : dry)
                {
                    HarnessLog.Line($"[standins] {(wet ? "sea" : "dry")} site at lat {lat} lon {lon}: terrain {terrain - home.MeanRadius:F0}m" +
                                    $"{(seaLevel != null ? $", sea level {seaLevel.Value - home.MeanRadius:F0}m" : "")} above the mean radius.");
                    return (dirCcf, terrain);
                }
            }
        }
        return null;
    }
}
