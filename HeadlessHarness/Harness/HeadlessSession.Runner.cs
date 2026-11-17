using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// The run's game rules as the runner restores them after each test. A test may change
// Universe.Rules, and a save load replaces the object outright (Universe.DeserializeSave sets Rules
// from the save), so the rules the run started with are captured once after the bring-up and put
// back when a test leaves them changed.
public sealed partial class HeadlessSession
{
    private static bool? _runRadiation;
    private static bool? _runLifeSupport;

    internal static void CaptureRunRules()
    {
        _runRadiation = Universe.Rules.Radiation;
        _runLifeSupport = Universe.Rules.LifeSupport;
    }

    internal static (bool Radiation, bool LifeSupport)? RunRules =>
        _runRadiation is bool r && _runLifeSupport is bool l ? (r, l) : null;

    // Returns true when the rules had to be put back. With an explicit rules request ApplyRules sets
    // them again and logs it; with "settings" the captured values are the profile's, so they are
    // written back directly.
    internal static bool RestoreRunRules(string after)
    {
        if (RunRules is not { } run)
            return false;
        (bool radiation, bool lifeSupport) = run;
        GameRules rules = Universe.Rules;
        if (rules.Radiation == radiation && rules.LifeSupport == lifeSupport)
            return false;
        if (_rulesRequest is { KeepSettings: true })
        {
            rules.Radiation = radiation;
            rules.LifeSupport = lifeSupport;
            HarnessLog.Line($"[harness] game rules after {after}: put back to radiation={(radiation ? "on" : "off")}, " +
                            $"life support={(lifeSupport ? "on" : "off")} (as loaded).");
        }
        else
        {
            ApplyRules(after);
        }
        return true;
    }
}
