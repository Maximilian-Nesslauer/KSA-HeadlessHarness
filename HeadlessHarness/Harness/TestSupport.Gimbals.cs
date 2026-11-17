using System.Collections.Generic;
using Brutal.Numerics;
using KSA;

namespace HeadlessHarness.Harness;

// One thrust-vector gimbal as the solver last left it. Angles are in radians about the gimbal's Y
// and Z axes (X holds the Y-axis value, Y the Z-axis value), rates in rad/s.
//   Angles, AngleRates: measured, from GimbalControllerState, which Gimbal.UpdateModules copies from
//     the gimbal's own GimbalState after each filter update.
//   Command: the normalized command the flight computer wrote (GimbalControllerState.CommandY and
//     CommandZ, -1..1 after clamping).
//   CommandAngles: the angle target the filter steers to, Command scaled by MaxAngles and limited to
//     the unit circle when ConstrainToCircle is set, as Gimbal.GetCommand computes it.
//   NaturalFrequency: the filter frequency in rad/s that applies, the gimbal's own frequency limited
//     by the controller's NaturalFrequencyLimit.
//   TotalThrust: the thrust in N of the nozzles on this gimbal in the last solver pass.
public readonly record struct GimbalReading(
    GimbalController Controller,
    float2 Angles,
    float2 AngleRates,
    float2 Command,
    float2 CommandAngles,
    float2 MaxAngles,
    float NaturalFrequency,
    float TotalThrust);

public static partial class TestSupport
{
    // Reads every gimbal that moves a nozzle of the engine. The path is the one Rocket.UpdateRockets
    // takes: EngineController.Rockets, Rocket.Nozzles, the nozzle's part's GimbalAsmb, and its
    // GimbalController. Several nozzles on one gimbal give one reading. An engine without a gimbal
    // gives an empty list. Read between SimDriver steps only.
    public static IReadOnlyList<GimbalReading> ReadGimbalAngles(Vehicle vehicle, EngineController engine)
    {
        RequireSameTree(vehicle, engine);
        List<GimbalReading> readings = new();
        if (!ModuleStateful<GimbalController, GimbalControllerState, EmptyStruct, EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var states)
            || states == null)
            return readings;

        HashSet<GimbalController> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Rocket rocket in engine.Rockets)
        {
            foreach (RocketNozzle nozzle in rocket.Nozzles)
            {
                Gimbal? gimbal = nozzle.Parent.GimbalAsmb;
                if (gimbal == null || !seen.Add(gimbal.Controller))
                    continue;
                GimbalControllerState state = states.GetState(gimbal.Controller);
                float2 maxAngles = new float2(gimbal.AxisY.MaxAngle, gimbal.AxisZ.MaxAngle);
                float2 command = new float2(Math.Clamp(state.CommandY, -1f, 1f), Math.Clamp(state.CommandZ, -1f, 1f));
                readings.Add(new GimbalReading(
                    gimbal.Controller,
                    state.Angles,
                    state.AngleRates,
                    command,
                    CommandAngles(gimbal, command, maxAngles),
                    maxAngles,
                    MathF.Min(gimbal.NaturalFrequency, state.NaturalFrequencyLimit),
                    state.TotalThrust));
            }
        }
        return readings;
    }

    // Gimbal.GetCommand is private, so its scaling is repeated here. Re-check it on a game update.
    private static float2 CommandAngles(Gimbal gimbal, float2 command, float2 maxAngles)
    {
        float y = command.X;
        float z = command.Y;
        if (gimbal.ConstrainToCircle)
        {
            float lengthSquared = y * y + z * z;
            if (lengthSquared > 1f)
            {
                float length = MathF.Sqrt(lengthSquared);
                y /= length;
                z /= length;
            }
        }
        return new float2(y * maxAngles.X, z * maxAngles.Y);
    }
}
