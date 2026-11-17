using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;
using KSA.Aero;

namespace HeadlessHarness.Harness;

// Checks AeroProbe on Test Vehicle 1 in its published aerodynamic mesh:
//   1. Along the stack axis the axisymmetric vehicle has no lift and no side force, and lift at a
//      small angle of attack is antisymmetric in the angle.
//   2. Drag grows with dynamic pressure, with a drag area that stays in a narrow band.
//   3. Above the atmosphere the loads are zero.
//   4. The probe leaves no trace: the live AeroSurfaceFlow of the tree has the same object, cached
//      flow direction and running length and exposure buffers before and after, and repeating the
//      first measurement after all others gives the same bits.
// Skips when the save is missing or the home body has no atmosphere.
public sealed class AeroProbeTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;
    private const double ProbeAltitudeM = 10_000.0;
    private const double ProbeMach = 2.0;
    private const double SmallAngleRad = 5.0 * Math.PI / 180.0;
    private const double ZeroLiftTol = 0.02;       // of drag
    private const double AntisymmetryTol = 0.1;    // relative; the panel mesh is not exactly symmetric
    private const double DragAreaBand = 2.0;       // max/min drag per dynamic pressure over the Mach sweep

    private static FieldInfo? _surfaceFlowField;
    private static FieldInfo? _lastFlowDirectionField;

    public string Name => "aero-probe";

    public int Run(HeadlessSession session)
    {
        if (!PropulsionTestVehicles.SaveExists())
        {
            HarnessLog.Line($"[aero-probe] SKIP: no '{PropulsionTestVehicles.SaveId}' save in {VehicleSaves.SaveFolderPath}.");
            return 0;
        }
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home || home.GetPhysicalAtmosphere() == null)
        {
            HarnessLog.Line("[aero-probe] SKIP: the home body has no atmosphere.");
            return 0;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool ok = true;
        try
        {
            Vehicle vehicle = PropulsionTestVehicles.SpawnInOrbit(system, home, "HarnessAeroProbeTest", SpawnAltitudeM);
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);
            session.CreateDriver().Step(1.0);

            Aerodynamics? aerodynamics = vehicle.Parts.AwaitAerodynamics();
            if (aerodynamics == null || aerodynamics.Mesh == null || aerodynamics.Mesh.Surface.FaceCount == 0)
            {
                HarnessLog.Line($"[aero-probe] FAIL: '{PropulsionTestVehicles.SaveId}' has no aerodynamic mesh after a step " +
                                $"(build failed: {vehicle.Parts.HasAerodynamicsBuildFailed}).");
                return 1;
            }

            // The live flow is primed with the axial direction first, so the comparison sees a flow
            // with a cached direction that a probe at another angle would replace.
            AeroSurfaceFlow liveFlow = aerodynamics.GetSurfaceFlow()!;
            liveFlow.Update(Double3Ex.ToBepu(-double3.UnitX.Transform(vehicle.Helm2Body)));
            FlowSnapshot before = FlowSnapshot.Take(aerodynamics);

            double speed = ProbeMach * AeroProbe.Atmosphere(home, ProbeAltitudeM).SpeedOfSound;
            AeroLoads axial = AeroProbe.Measure(vehicle, home, ProbeAltitudeM, speed, 0.0, 0.0);
            ok &= CheckAxial(axial);
            ok &= CheckAngleOfAttack(vehicle, home, speed, axial);
            ok &= CheckDynamicPressure(vehicle, home);
            ok &= CheckAboveAtmosphere(vehicle, home, speed);

            AeroLoads repeat = AeroProbe.Measure(vehicle, home, ProbeAltitudeM, speed, 0.0, 0.0);
            bool repeatOk = repeat.ForceBody.Equals(axial.ForceBody) && repeat.MomentBody.Equals(axial.MomentBody);
            HarnessLog.Line($"[aero-probe] repeated axial measurement is bit-identical => {TestSupport.Verdict(repeatOk)}");
            ok &= repeatOk;

            FlowSnapshot after = FlowSnapshot.Take(aerodynamics);
            bool untouched = before.Equals(after) && ReferenceEquals(vehicle.Parts.Aerodynamics, aerodynamics);
            HarnessLog.Line($"[aero-probe] live flow before {before} after {after} => {TestSupport.Verdict(untouched)}");
            ok &= untouched;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        HarnessLog.Line($"[aero-probe] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckAxial(in AeroLoads axial)
    {
        double limit = ZeroLiftTol * axial.Drag;
        bool ok = axial.Drag > 0.0 && Math.Abs(axial.Lift) <= limit && Math.Abs(axial.SideForce) <= limit;
        HarnessLog.Line($"[aero-probe] axial at {ProbeAltitudeM / 1000:F0}km Mach {axial.Mach:F2} (q={axial.DynamicPressure:F0}Pa, " +
                        $"{axial.FaceCount} faces, panel width {axial.PanelWidth:G4}m): drag {axial.Drag:F1}N lift {axial.Lift:F2}N " +
                        $"side {axial.SideForce:F2}N (tol {limit:F2}N) => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool CheckAngleOfAttack(Vehicle vehicle, IParentBody home, double speed, in AeroLoads axial)
    {
        AeroLoads up = AeroProbe.Measure(vehicle, home, ProbeAltitudeM, speed, SmallAngleRad, 0.0);
        AeroLoads down = AeroProbe.Measure(vehicle, home, ProbeAltitudeM, speed, -SmallAngleRad, 0.0);
        double magnitude = Math.Max(Math.Abs(up.Lift), Math.Abs(down.Lift));
        bool ok = magnitude > Math.Abs(axial.Lift)
                  && Math.Abs(up.Lift + down.Lift) <= AntisymmetryTol * magnitude
                  && Math.Abs(up.Drag - down.Drag) <= AntisymmetryTol * up.Drag;
        HarnessLog.Line($"[aero-probe] angle of attack +-{SmallAngleRad * 180 / Math.PI:F0}deg: lift {up.Lift:F1}N / {down.Lift:F1}N, " +
                        $"drag {up.Drag:F1}N / {down.Drag:F1}N, pitch moment {up.MomentBody.Y:F1}Nm / {down.MomentBody.Y:F1}Nm => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool CheckDynamicPressure(Vehicle vehicle, IParentBody home)
    {
        double[] machs = { 1.5, 3.0, 6.0 };
        bool ok = true;
        double previousDrag = 0.0;
        double minArea = double.MaxValue;
        double maxArea = 0.0;
        foreach (double mach in machs)
        {
            AeroLoads loads = AeroProbe.MeasureAtMach(vehicle, home, ProbeAltitudeM, mach, 0.0, 0.0);
            ok &= loads.Drag > previousDrag;
            previousDrag = loads.Drag;
            double area = loads.Drag / loads.DynamicPressure;
            minArea = Math.Min(minArea, area);
            maxArea = Math.Max(maxArea, area);
            HarnessLog.Line($"[aero-probe]   Mach {mach:F1}: q={loads.DynamicPressure:F0}Pa drag {loads.Drag:F1}N drag area {area:F4}m^2");
        }
        ok &= maxArea <= DragAreaBand * minArea;
        HarnessLog.Line($"[aero-probe] drag grows with dynamic pressure, drag area {minArea:F4}..{maxArea:F4}m^2 => {TestSupport.Verdict(ok)}");
        return ok;
    }

    private static bool CheckAboveAtmosphere(Vehicle vehicle, IParentBody home, double speed)
    {
        double altitude = home.GetPhysicalAtmosphere()!.Height + 1000.0;
        AeroLoads loads = AeroProbe.Measure(vehicle, home, altitude, speed, SmallAngleRad, 0.0);
        bool ok = !loads.Atmosphere.HasAtmosphere && loads.ForceBody.Equals(double3.Zero) && loads.MomentBody.Equals(double3.Zero);
        HarnessLog.Line($"[aero-probe] above the atmosphere ({altitude / 1000:F0}km): force {loads.ForceBody.Length():G3}N => {TestSupport.Verdict(ok)}");
        return ok;
    }

    // What the live flow of a tree holds between solver steps. Aerodynamics._surfaceFlow and
    // AeroSurfaceFlow._lastFlowDirection are private; reading them through the public
    // Aerodynamics.GetSurfaceFlow would create a flow where there was none.
    private readonly record struct FlowSnapshot(object? Flow, Vector3? LastDirection, ulong BufferHash)
    {
        public static FlowSnapshot Take(Aerodynamics aerodynamics)
        {
            _surfaceFlowField ??= AccessTools.Field(typeof(Aerodynamics), "_surfaceFlow")
                ?? throw new InvalidOperationException("[HeadlessHarness] Aerodynamics._surfaceFlow not found - game version may have changed.");
            _lastFlowDirectionField ??= AccessTools.Field(typeof(AeroSurfaceFlow), "_lastFlowDirection")
                ?? throw new InvalidOperationException("[HeadlessHarness] AeroSurfaceFlow._lastFlowDirection not found - game version may have changed.");
            AeroSurfaceFlow? flow = (AeroSurfaceFlow?)_surfaceFlowField.GetValue(aerodynamics);
            if (flow == null)
                return new FlowSnapshot(null, null, 0);
            ulong hash = 14695981039346656037UL;
            hash = Hash(hash, flow.SolidRunningLengths);
            hash = Hash(hash, flow.SheetFrontRunningLengths);
            hash = Hash(hash, flow.SheetBackRunningLengths);
            hash = Hash(hash, flow.SolidExposedFractions);
            hash = Hash(hash, flow.SheetFrontExposedFractions);
            hash = Hash(hash, flow.SheetBackExposedFractions);
            return new FlowSnapshot(flow, (Vector3?)_lastFlowDirectionField.GetValue(flow), hash);
        }

        private static ulong Hash(ulong hash, Span<Vector<float>> values)
        {
            foreach (uint bits in MemoryMarshal.Cast<Vector<float>, uint>(values))
                hash = (hash ^ bits) * 1099511628211UL;
            return hash;
        }

        public override string ToString() => $"(flow {(Flow == null ? "none" : "set")}, direction {LastDirection}, hash {BufferHash:X16})";
    }
}
