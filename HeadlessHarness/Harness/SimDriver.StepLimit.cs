using System.Reflection;
using HarmonyLib;
using KSA;

namespace HeadlessHarness.Harness;

public sealed partial class SimDriver
{
    private static FieldInfo? _stepLimitTimeField;

    // Off by default. On, each step ends no later than the time the game asked to stop at
    // (Universe.LimitNextStep), as Universe.GetJobSimStep shortens a frame in the running game:
    // the probe power warning and its run-out, the next life support rung, the next crew dose
    // threshold. A large dt then lands on those events instead of stepping over them. Steps can be
    // shorter than dt, so loop on Elapsed rather than on a step count.
    public bool ClipToGameStepLimit { get; set; }

    // Whether the last Step was shortened to the game's step limit.
    public bool LastStepClipped { get; private set; }

    // The time the game asked the next step not to pass, or null when nothing asked. Every
    // Universe.ApplyVehicleSolvers clears the limit before the vehicles set it again, so between
    // SimDriver steps it holds the requests of the last step.
    public static UniverseTime? NextGameStepLimit
    {
        get
        {
            _stepLimitTimeField ??= BindStepLimitField();
            UniverseTime limit = (UniverseTime)_stepLimitTimeField.GetValue(null)!;
            return limit == UniverseTime.MaxValue ? null : limit;
        }
    }

    partial void BeforeStep(ref double dt)
    {
        LastStepClipped = false;
        if (!ClipToGameStepLimit || NextGameStepLimit is not UniverseTime limit)
            return;
        // The same test as Universe.GetJobSimStep: a limit at or before the start of the step is
        // stale and ignored.
        if (Elapsed + dt > limit && limit > Elapsed)
        {
            dt = (limit - Elapsed).Seconds();
            LastStepClipped = true;
        }
    }

    private static FieldInfo BindStepLimitField()
    {
        FieldInfo field = AccessTools.Field(typeof(Universe), "_stepLimitTime");
        if (field == null || !field.IsStatic || field.FieldType != typeof(UniverseTime))
            throw new InvalidOperationException(
                "[HeadlessHarness] Universe._stepLimitTime not found - game version may have changed.");
        return field;
    }
}
