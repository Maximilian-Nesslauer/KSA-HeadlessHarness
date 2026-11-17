using System.Collections.Generic;
using System.Globalization;
using HeadlessHarness.Core;

namespace HeadlessHarness.Harness;

// Test parameters from one environment variable, so a test reads its tunables (a step size, a
// sample count, a vehicle name) without an environment variable of its own. KSA_HEADLESS_PARAMS
// holds "name=value;name2=value2"; names compare without case, the last entry of a name wins, and
// a value cannot hold ';'. Every value a test reads is logged once per test, with whether it came
// from the variable or from the default, so a run log always shows the values it ran with. Numbers
// parse with the invariant culture.
public static partial class TestSupport
{
    public const string ParamsEnvVar = "KSA_HEADLESS_PARAMS";

    private static readonly object ParamGate = new();
    private static readonly HashSet<string> ParamsRead = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ParamsLogged = new(StringComparer.OrdinalIgnoreCase);
    private static string? _paramsLoggedFor;

    public static string Param(string name, string defaultValue)
    {
        return TryGetParam(name, out string? value) ? LogParam(name, value!, true) : LogParam(name, defaultValue, false);
    }

    public static int Param(string name, int defaultValue)
    {
        if (!TryGetParam(name, out string? raw))
            return LogParam(name, defaultValue, false);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw BadParamValue(name, raw!, "an integer");
        return LogParam(name, value, true);
    }

    public static double Param(string name, double defaultValue)
    {
        if (!TryGetParam(name, out string? raw))
            return LogParam(name, defaultValue, false);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw BadParamValue(name, raw!, "a number");
        return LogParam(name, value, true);
    }

    // true, false, 1, 0, yes, no, on, off, in any case.
    public static bool Param(string name, bool defaultValue)
    {
        if (!TryGetParam(name, out string? raw))
            return LogParam(name, defaultValue, false);
        switch (raw!.ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "on":
                return LogParam(name, true, true);
            case "false": case "0": case "no": case "off":
                return LogParam(name, false, true);
            default:
                throw BadParamValue(name, raw, "a boolean (true/false, 1/0, yes/no, on/off)");
        }
    }

    public static bool HasParam(string name) => TryGetParam(name, out _);

    // Throws InvalidOperationException for an entry without '=' or with an empty name, because a
    // malformed list would otherwise run the test with its defaults and look like a pass.
    internal static Dictionary<string, string> ParseParams(string? raw)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return values;
        foreach (string entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=');
            string key = eq > 0 ? entry[..eq].Trim() : string.Empty;
            if (eq < 0 || key.Length == 0)
                throw new InvalidOperationException(
                    $"[HeadlessHarness] {ParamsEnvVar} entry '{entry}' is not of the form name=value.");
            values[key] = entry[(eq + 1)..].Trim();
        }
        return values;
    }

    // The names in KSA_HEADLESS_PARAMS that no test read so far, for the typo warning at run end.
    internal static IReadOnlyList<string> UnreadParams()
    {
        List<string> unread = new();
        lock (ParamGate)
        {
            foreach (string key in ParseParams(Environment.GetEnvironmentVariable(ParamsEnvVar)).Keys)
            {
                if (!ParamsRead.Contains(key))
                    unread.Add(key);
            }
        }
        return unread;
    }

    private static bool TryGetParam(string name, out string? value)
    {
        Dictionary<string, string> values = ParseParams(Environment.GetEnvironmentVariable(ParamsEnvVar));
        lock (ParamGate)
            ParamsRead.Add(name);
        return values.TryGetValue(name, out value);
    }

    private static T LogParam<T>(string name, T value, bool fromEnv)
    {
        string test = HarnessRun.CurrentTest ?? "harness";
        lock (ParamGate)
        {
            if (!string.Equals(_paramsLoggedFor, test, StringComparison.Ordinal))
            {
                _paramsLoggedFor = test;
                ParamsLogged.Clear();
            }
            if (!ParamsLogged.Add(name))
                return value;
        }
        string text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
        HarnessLog.Line($"[{test}] param {name} = {text} ({(fromEnv ? ParamsEnvVar : "default")})");
        return value;
    }

    private static InvalidOperationException BadParamValue(string name, string raw, string expected) =>
        new($"[HeadlessHarness] {ParamsEnvVar}: '{name}={raw}' is not {expected}.");
}
