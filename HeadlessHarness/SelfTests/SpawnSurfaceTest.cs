using System.Collections.Generic;
using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace HeadlessHarness.SelfTests;

// Lands the builder's probe craft on the surface and checks that it stays put: on the home body's
// launch pad (SpawnAtLaunchSite) and on open ground turned to heading 90 (SpawnAtLocation), and on
// the Moon at a named landmark and at an arbitrary point. Each spawn settles for a moment, then
// must not move more than a few centimetres over 30 s in the body-fixed frame. Opt-in, because
// stepping 2 minutes of contact at a small step takes a while. A body or location the loaded
// system lacks is skipped.
public sealed class SpawnSurfaceTest : IHarnessTest
{
    private const double Dt = 0.02;
    private const double SettleSeconds = 2.0;
    private const double HoldSeconds = 30.0;
    private const double DriftTol = 0.05;        // m over the hold, body-fixed
    private const double HeadingTol = 0.999;     // cosine between body +Z and the asked heading
    private const string MoonId = "Luna";
    private const string MoonLandmark = "Apollo11";
    // Open, level ground near Madrid on Earth, away from every launch site.
    private const double OpenLatitude = 40.4168;
    private const double OpenLongitude = -3.7038;

    public string Name => "spawn-surface";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        bool ok = true;
        int ran = 0;
        try
        {
            if (system.HomeBody is Celestial home)
            {
                if (home.GetLocation(VehicleLaunchMenu.StartingLocation) != null)
                {
                    Vehicle pad = VehicleSpawner.SpawnAtLaunchSite(VehicleBuilder.ProbeCraft(), home, VehicleLaunchMenu.StartingLocation, "HarnessSurfacePad");
                    ok &= Hold(driver, pad, home, $"{home.Id} pad '{VehicleLaunchMenu.StartingLocation}'");
                    ran++;
                }
                else
                {
                    HarnessLog.Line($"[spawn-surface] SKIP pad: {home.Id} has no location '{VehicleLaunchMenu.StartingLocation}'.");
                }

                if (home.Id == "Earth")
                {
                    Vehicle open = VehicleSpawner.SpawnAtLocation(VehicleBuilder.ProbeCraft(), home, OpenLatitude, OpenLongitude, 90.0, "HarnessSurfaceOpen", 0.1);
                    ok &= CheckHeading(open, home, 90.0);
                    ok &= Hold(driver, open, home, $"{home.Id} open ground, heading 90, dropped 0.1 m");
                    ran++;
                }
            }

            if (system.All.Get(MoonId) is Celestial moon)
            {
                if (moon.GetLocation(MoonLandmark) != null)
                {
                    Vehicle site = VehicleSpawner.SpawnAtLaunchSite(VehicleBuilder.ProbeCraft(), moon, MoonLandmark, "HarnessSurfaceMoonSite");
                    ok &= Hold(driver, site, moon, $"{moon.Id} '{MoonLandmark}'");
                    ran++;
                }
                Vehicle plain = VehicleSpawner.SpawnAtLocation(VehicleBuilder.ProbeCraft(), moon, 0.0, 0.0, 45.0, "HarnessSurfaceMoon");
                ok &= CheckHeading(plain, moon, 45.0);
                ok &= Hold(driver, plain, moon, $"{moon.Id} at 0/0, heading 45");
                ran++;
            }
            else
            {
                HarnessLog.Line($"[spawn-surface] SKIP moon: the loaded system has no body '{MoonId}'.");
            }
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        if (ran == 0)
        {
            HarnessLog.Line("[spawn-surface] SKIP: the loaded system offers none of the surface spots.");
            return 0;
        }
        HarnessLog.Line($"[spawn-surface] {ran} surface spawn(s) => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    // Body +Z must point along the compass heading: north turned toward east by the heading.
    private static bool CheckHeading(Vehicle vehicle, Celestial body, double headingDeg)
    {
        // Vehicle.GetPositionCce reads a per-frame cache that is empty until the first step, so the
        // spawn position comes from the orbit the vehicle was created with.
        double3 up = VehicleBuilder.Rotate(vehicle.Orbit.StateVectors.PositionCci, body.GetCci2Cce()).Normalized();
        double3 pole = VehicleBuilder.Rotate(double3.UnitZ, body.GetCci2Cce());
        double3 east = double3.Cross(pole, up).Normalized();
        double3 north = double3.Cross(up, east);
        double h = headingDeg * Math.PI / 180.0;
        double3 want = north * Math.Cos(h) + east * Math.Sin(h);
        double3 bodyZ = VehicleBuilder.Rotate(double3.UnitZ, vehicle.Body2Cce);
        double3 bodyX = VehicleBuilder.Rotate(double3.UnitX, vehicle.Body2Cce);
        double cosHeading = double3.Dot(bodyZ, want);
        double cosUp = double3.Dot(bodyX, up);
        bool ok = cosHeading > HeadingTol && cosUp > HeadingTol;
        HarnessLog.Line($"[spawn-surface] '{vehicle.Id}' heading {headingDeg}: cos(+Z, heading)={cosHeading:F6} cos(+X, up)={cosUp:F6} => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool Hold(SimDriver driver, Vehicle vehicle, Celestial body, string label)
    {
        double startRadius = vehicle.Orbit.StateVectors.PositionCci.Length();
        driver.Step(Dt, (int)Math.Round(SettleSeconds / Dt));
        double3 p0 = BodyFixed(vehicle, body, driver.Elapsed);
        double3 c0 = vehicle.GetPositionCce();
        driver.Step(Dt, (int)Math.Round(HoldSeconds / Dt));
        bool alive = !vehicle.IsDisposed;
        double3 p1 = alive ? BodyFixed(vehicle, body, driver.Elapsed) : p0;
        double drift = (p1 - p0).Length();
        // Carried by the turning body, the vehicle must move in the inertial-axis frame; a
        // position that did not update would read as no drift.
        double carried = alive ? (vehicle.GetPositionCce() - c0).Length() : 0.0;
        double3 spinCce = VehicleBuilder.Rotate(((IParentBody)body).GetAngularVelocityCci(), body.GetCci2Cce());
        double expectedCarried = double3.Cross(spinCce, c0).Length() * HoldSeconds;
        double terrain = body.MeanRadius + body.GetTerrainHeightFromDirCcf(p1.Normalized());
        double heightAboveTerrain = p1.Length() - terrain;
        bool ok = alive && drift < DriftTol && Math.Abs(carried - expectedCarried) < 0.01 * expectedCarried + DriftTol;
        HarnessLog.Line($"[spawn-surface] {label}: settled {p1.Length() - startRadius:+0.000;-0.000} m from the spawn radius, " +
                        $"drift {drift:F4} m over {HoldSeconds:F0} s (carried {carried:F1} m by the rotation, expect about {expectedCarried:F1} m), centre of mass {heightAboveTerrain:F3} m above the plain terrain, " +
                        $"situation {(alive ? vehicle.Situation.ToString() : "disposed")} => {TestSupport.Verdict(ok)}");
        VehicleSpawner.Despawn(vehicle);
        return ok;
    }

    // The vehicle position in the body-fixed frame at the driver's time. The no-argument
    // Celestial.GetCcf2Cce is moved only by the render update, so the time is passed.
    private static double3 BodyFixed(Vehicle vehicle, Celestial body, UniverseTime time)
        => VehicleBuilder.Rotate(vehicle.GetPositionCce(), body.GetCcf2Cce(time).Inverse());
}
