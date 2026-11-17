using System.Collections.Generic;
using System.Globalization;
using Brutal.Numerics;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;
using KSA.Kfs.Host.Workbench;

namespace HeadlessHarness.SelfTests;

// Flies the game's kFS example "prograde hold" (Examples.KC_ATTITUDE) on the flight-test vehicle in a
// circular orbit: the program owns attitude control and points the nose (body +X) along the velocity
// with the RCS thrusters. The pointing error must converge. The flight runs twice from the same
// state, and the two end states must match bit for bit, which shows that the kFS ticks depend on sim
// time only. Opt-in, because kFS is a prototype and the hold takes minutes of sim time.
public sealed class KfsProgradeHoldTest : IHarnessTest
{
    private const double SpawnAltitudeM = 500_000.0;
    private const double Dt = 0.1;
    private const double HoldSeconds = 300.0;
    private const double SampleSeconds = 10.0;
    private const double ConvergedDeg = 2.0;
    // The last part of the hold, over which the error must stay converged.
    private const double TailSeconds = 60.0;

    public string Name => "kfs-prograde-hold";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        string? saveId = SteppingSelfTestSupport.ResolveFlightSave(Name);
        if (saveId == null)
            return 0;
        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home)
        {
            HarnessLog.Line("[kfs-prograde-hold] SKIP: the loaded system has no home body.");
            return 0;
        }

        bool forceOffRails = PhysicsBubble._forceOffRails;
        bool ok = true;
        try
        {
            using IDisposable kfs = Kfs.Enable();
            // The program commands thruster pulses only while a step simulates the vehicle off rails.
            PhysicsBubble._forceOffRails = true;

            Flight? first = Fly(session, system, home, saveId, "run 1");
            Flight? second = Fly(session, system, home, saveId, "run 2");
            ok &= first is { Converged: true } && second is { Converged: true };
            if (first != null && second != null)
            {
                bool same = first.EndState == second.EndState;
                HarnessLog.Line(same
                    ? "[kfs-prograde-hold] determinism: the two runs end bit for bit the same => PASS"
                    : $"[kfs-prograde-hold] determinism: the runs differ => FAIL\n  run 1 {first.EndState}\n  run 2 {second.EndState}");
                ok &= same;
            }
        }
        finally
        {
            PhysicsBubble._forceOffRails = forceOffRails;
            Kfs.DetachAll();
        }

        HarnessLog.Line($"[kfs-prograde-hold] => {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private sealed record Flight(bool Converged, string EndState);

    private static Flight? Fly(HeadlessSession session, CelestialSystem system, IParentBody home, string saveId, string label)
    {
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        SimDriver driver = session.CreateDriver();
        SteppingSelfTestSupport.AlignToLattice(driver);
        try
        {
            Orbit orbit = VehicleSpawner.CircularCci(home, home.MeanRadius + SpawnAltitudeM, driver.Elapsed);
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "HarnessKfsHold", orbit);
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            TestSupport.SetManualControlInputs(vehicle, 0f, engineOn: false);

            foreach (string path in new[] { "../escaped.c", "..\\escaped.c" })
            {
                try
                {
                    using KfsProgram escaped = Kfs.LoadSource(vehicle, path, "void main() {}");
                    throw new InvalidOperationException("kFS accepted a path outside its workspace.");
                }
                catch (ArgumentException)
                {
                    HarnessLog.Line($"[kfs-prograde-hold] refused workspace escape '{path}' => PASS");
                }
            }

            using KfsProgram program = Kfs.LoadExample(vehicle, Examples.KC_ATTITUDE);
            HarnessLog.Line($"[kfs-prograde-hold] {label}: loaded '{program.File}' ({program.Build.Language}, " +
                            $"{program.Build.CodeSize} bytes of code) onto '{vehicle.Id}' with " +
                            $"{program.Host.Thrusters.Length} thruster controller(s).");
            program.Start();
            if (!driver.StepUntil(() => program.UnderKfsControl, Dt, 5.0, () => $"kFS did not take control (state {program.State})"))
            {
                program.LogConsole();
                return null;
            }

            double startDeg = PointingErrorDeg(vehicle);
            double tailMaxDeg = 0.0;
            double held = 0.0;
            double nextSample = 0.0;
            string trace = "";
            driver.StepFor(HoldSeconds, Dt, () =>
            {
                held += Dt;
                double error = PointingErrorDeg(vehicle);
                if (held >= HoldSeconds - TailSeconds)
                    tailMaxDeg = Math.Max(tailMaxDeg, error);
                if (held + 1e-9 >= nextSample)
                {
                    trace += string.Create(CultureInfo.InvariantCulture, $" {held:F0}s:{error:F2}");
                    nextSample += SampleSeconds;
                }
            });
            double endDeg = PointingErrorDeg(vehicle);
            bool stillControlled = program.UnderKfsControl;
            bool converged = stillControlled && endDeg < ConvergedDeg && tailMaxDeg < ConvergedDeg && endDeg < startDeg;
            HarnessLog.Line($"[kfs-prograde-hold] {label}: pointing error (deg) over time:{trace}");
            HarnessLog.Line(string.Create(CultureInfo.InvariantCulture,
                $"[kfs-prograde-hold] {label}: error {startDeg:F2} deg at start, {endDeg:F3} deg after {HoldSeconds:F0}s, " +
                $"at most {tailMaxDeg:F3} deg over the last {TailSeconds:F0}s (limit {ConvergedDeg:F1}), rates {vehicle.BodyRates.Length():E2} rad/s, " +
                $"kFS {(stillControlled ? "in control" : "not in control")}, state {program.State} => {TestSupport.Verdict(converged)}"));
            if (!converged)
                program.LogConsole();
            string endState = EndState(vehicle, program);

            // kfs stop hands the vehicle back to the stock flight computer at the next step.
            program.Stop();
            driver.Step(Dt);
            if (program.UnderKfsControl)
            {
                HarnessLog.Line($"[kfs-prograde-hold] {label}: kFS still in control one step after the stop => FAIL");
                converged = false;
            }
            return new Flight(converged, endState);
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
    }

    // The angle between the nose (body +X) and the velocity relative to the parent, both in CCI, the
    // error the example program drives to zero.
    private static double PointingErrorDeg(Vehicle vehicle)
    {
        double3 nose = SteppingSelfTestSupport.Rotate(double3.UnitX, vehicle.GetBody2Cci());
        double3 velocity = vehicle.GetVelocityCci();
        double cos = double3.Dot(nose.Normalized(), velocity.Normalized());
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * (180.0 / Math.PI);
    }

    private static string EndState(Vehicle vehicle, KfsProgram program)
    {
        double3 position = vehicle.GetPositionCci();
        double3 velocity = vehicle.GetVelocityCci();
        doubleQuat attitude = vehicle.GetBody2Cci();
        double3 rates = vehicle.BodyRates;
        return string.Join(",",
            Bits(position.X), Bits(position.Y), Bits(position.Z),
            Bits(velocity.X), Bits(velocity.Y), Bits(velocity.Z),
            Bits(attitude.X), Bits(attitude.Y), Bits(attitude.Z), Bits(attitude.W),
            Bits(rates.X), Bits(rates.Y), Bits(rates.Z),
            Bits(vehicle.TotalMass), program.Computer.LastK.ToString(CultureInfo.InvariantCulture));
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);
}
