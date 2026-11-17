using HeadlessHarness.Core;
using HeadlessHarness.Harness;

namespace HeadlessHarness.SelfTests;

// Test parameters: TestSupport.Param with each type against a KSA_HEADLESS_PARAMS value this test
// sets itself and puts back, the parse rules (last entry wins, names without case, trimmed values),
// the errors for a malformed list or a bad value, and the run settings HarnessRun parses.
public sealed class RunnerParamsTest : IHarnessTest
{
    public string Name => "runner-params";

    public int Run(HeadlessSession session)
    {
        HarnessChecks t = new(Name);
        string? saved = Environment.GetEnvironmentVariable(TestSupport.ParamsEnvVar);
        try
        {
            Set(" alpha=1; beta=2.5 ;flag=yes;Name=hello world;alpha=3;empty=;eq=a=b");
            t.Check("the last entry of a name wins", TestSupport.Param("alpha", 0) == 3);
            t.Check("names compare without case", TestSupport.Param("ALPHA", 0) == 3);
            t.Check("double", TestSupport.Param("beta", 0.0) == 2.5);
            t.Check("bool", TestSupport.Param("flag", false));
            t.Check("string with a space", TestSupport.Param("name", "x") == "hello world");
            t.Check("an empty value is a value", TestSupport.Param("empty", "default") == string.Empty);
            t.Check("a value may hold '='", TestSupport.Param("eq", "x") == "a=b");
            t.Check("defaults of unset names", TestSupport.Param("missing", 7) == 7 && TestSupport.Param("missing", 1.25) == 1.25
                && TestSupport.Param("missing", true) && TestSupport.Param("missing", "d") == "d");
            t.Check("HasParam", TestSupport.HasParam("beta") && !TestSupport.HasParam("missing"));

            Set("count=abc;flag=maybe;ratio=1,5");
            t.Check("a bad integer throws", Throws(() => TestSupport.Param("count", 1)));
            t.Check("a bad boolean throws", Throws(() => TestSupport.Param("flag", false)));
            t.Check("a decimal comma is not a number", Throws(() => TestSupport.Param("ratio", 0.0)));

            Set("novalue");
            t.Check("an entry without '=' throws", Throws(() => TestSupport.Param("novalue", "x")));
            t.Check("an empty name throws", Throws(() => TestSupport.ParseParams("=x")));
            t.Check("blank lists parse empty", TestSupport.ParseParams(null).Count == 0 && TestSupport.ParseParams(" ; ;").Count == 0);

            Set(null);
            t.Check("unset uses the default", TestSupport.Param("alpha", 5) == 5 && !TestSupport.HasParam("alpha"));

            t.Check("seed parse", HarnessRun.TryParseSeed(" 42 ", out int? seed) && seed == 42
                && HarnessRun.TryParseSeed("", out int? none) && none == null
                && !HarnessRun.TryParseSeed("4x", out _));
            t.Check("seconds parse", HarnessRun.TryParseSeconds("2.5", out double? s) && s == 2.5
                && HarnessRun.TryParseSeconds(" 0 ", out double? zero) && zero == null
                && !HarnessRun.TryParseSeconds("-1", out _) && !HarnessRun.TryParseSeconds("ten", out _));
        }
        finally
        {
            Set(saved);
        }
        t.Check("the variable is put back", Environment.GetEnvironmentVariable(TestSupport.ParamsEnvVar) == saved);
        return t.Finish();
    }

    private static void Set(string? value) => Environment.SetEnvironmentVariable(TestSupport.ParamsEnvVar, value);

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool Throws(Func<object> func) => Throws(() => { func(); });
}
