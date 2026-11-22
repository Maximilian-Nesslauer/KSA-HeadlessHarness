using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace HeadlessHarness.Core;

// The machine-readable record of one harness run: <run log basename>-results.json and
// <run log basename>-junit.xml next to the run log. The runner fills in the run facts and opens and
// closes one entry per test; HarnessChecks feeds the check counts of the open entry, and HarnessLog
// feeds the lines that report a skip. The files are rewritten whole on every Write, so the watchdog
// thread can write them for a hung run while the test thread is still inside the test.
internal static class HarnessResults
{
    public const string OutcomePass = "pass";
    public const string OutcomeFail = "fail";
    public const string OutcomeSkip = "skip";
    public const string OutcomeInfra = "infra";
    public const string OutcomeTimeout = "timeout";

    private const int SchemaVersion = 1;

    private static readonly object Gate = new();
    private static readonly List<TestRecord> Tests = new();
    private static readonly List<string> OptInSkipped = new();
    private static readonly long RunStartTicks = Stopwatch.GetTimestamp();

    private static TestRecord? _current;
    private static volatile string? _skipPrefix;
    private static volatile string? _checkPrefix;
    private static int _checks;
    private static int _failedChecks;
    private static int _skips;
    private static int _logSkips;
    private static int _logChecks;
    private static int _infra;

    public static string? GameVersion { get; set; }
    public static string? HarnessVersion { get; set; }
    public static string? SystemId { get; set; }
    public static string? ProfilePath { get; set; }
    public static string? InstancePath { get; set; }
    public static string? RulesRequest { get; set; }
    public static bool? Radiation { get; set; }
    public static bool? LifeSupport { get; set; }
    public static bool? TerrainModifiers { get; set; }
    public static string? Seed { get; set; }
    public static string? Params { get; set; }
    public static string? TestFilter { get; set; }

    // Infrastructure failures that belong to no test: a consumer that failed to load, a filter
    // entry that matched nothing, a cleanup that threw.
    public static int RunInfrastructureFailures { get; set; }

    public static string ResultsPath => SidePath("results.json");

    public static string JUnitPath => SidePath("junit.xml");

    public sealed class TestRecord
    {
        public required string Name { get; init; }
        public required string Suite { get; init; }
        public required long StartTicks { get; init; }
        public required double StartEpochSeconds { get; init; }
        public required string StartEpochNanoseconds { get; init; }
        public string Outcome { get; set; } = OutcomeInfra;
        public double WallMs { get; set; }
        public double? SimSeconds { get; set; }
        public int Checks { get; set; }
        public int FailedChecks { get; set; }
        public int Skips { get; set; }
        public int LogSkips { get; set; }
        public int LogChecks { get; set; }
        public string? Detail { get; set; }
        public List<string> Leaks { get; } = new();
    }

    public static void AddOptInSkipped(IEnumerable<string> names)
    {
        lock (Gate)
            OptInSkipped.AddRange(names);
    }

    public static void BeginTest(string name, string suite, double startEpochSeconds, string startEpochNanoseconds)
    {
        lock (Gate)
        {
            _current = new TestRecord
            {
                Name = name,
                Suite = suite,
                StartTicks = Stopwatch.GetTimestamp(),
                StartEpochSeconds = startEpochSeconds,
                StartEpochNanoseconds = startEpochNanoseconds,
            };
            Tests.Add(_current);
            Interlocked.Exchange(ref _checks, 0);
            Interlocked.Exchange(ref _failedChecks, 0);
            Interlocked.Exchange(ref _skips, 0);
            Interlocked.Exchange(ref _logSkips, 0);
            Interlocked.Exchange(ref _logChecks, 0);
            Interlocked.Exchange(ref _infra, 0);
            _skipPrefix = $"[{name}] SKIP";
            _checkPrefix = $"[{name}] TEST ";
        }
    }

    public static int CurrentChecks => Volatile.Read(ref _checks);

    public static int CurrentFailedChecks => Volatile.Read(ref _failedChecks);

    public static int CurrentSkips => Volatile.Read(ref _skips) + Volatile.Read(ref _logSkips);

    public static int CurrentLogChecks => Volatile.Read(ref _logChecks);

    public static void CountCheck(bool pass)
    {
        if (_skipPrefix == null)
            return;
        Interlocked.Increment(ref _checks);
        if (!pass)
            Interlocked.Increment(ref _failedChecks);
    }

    public static int CurrentInfrastructureFailures => Volatile.Read(ref _infra);

    public static void CountInfra()
    {
        if (_skipPrefix != null)
            Interlocked.Increment(ref _infra);
    }

    public static void CountSkip()
    {
        if (_skipPrefix != null)
            Interlocked.Increment(ref _skips);
    }

    // A test without a check context reports a skip by logging "[<name>] SKIP...". A test with a check
    // context of its own that logs in the HarnessChecks format ("[<name>] TEST ... => PASS") is seen
    // through its lines too, so that a test with passed checks and one skipped case is not reported
    // as skipped. HarnessChecks counts itself, so its own lines are not counted again here.
    public static void ObserveLine(string line, bool fromChecks)
    {
        if (fromChecks)
            return;
        string? skipPrefix = _skipPrefix;
        if (skipPrefix != null && line.StartsWith(skipPrefix, StringComparison.Ordinal))
            Interlocked.Increment(ref _logSkips);
        string? checkPrefix = _checkPrefix;
        if (checkPrefix != null && line.StartsWith(checkPrefix, StringComparison.Ordinal)
            && (line.EndsWith("=> PASS", StringComparison.Ordinal) || line.EndsWith("=> FAIL", StringComparison.Ordinal)))
            Interlocked.Increment(ref _logChecks);
    }

    public static void AddLeak(string vehicleId)
    {
        lock (Gate)
            _current?.Leaks.Add(vehicleId);
    }

    // Closes the open entry. A test that returned 0 is a skip when it recorded no check and reported
    // at least one skip; a test that returned 0 but recorded a failed check is a failure.
    public static TestRecord? EndTest(string outcome, double wallMs, double? simSeconds, string? detail)
    {
        lock (Gate)
        {
            TestRecord? record = _current;
            if (record == null)
                return null;
            record.Checks = CurrentChecks;
            record.FailedChecks = CurrentFailedChecks;
            record.Skips = Volatile.Read(ref _skips);
            record.LogSkips = Volatile.Read(ref _logSkips);
            record.LogChecks = Volatile.Read(ref _logChecks);
            int infra = Volatile.Read(ref _infra);
            record.Outcome = outcome == OutcomePass
                ? DecideOutcome(true, record.Checks + record.LogChecks, record.FailedChecks, record.Skips + record.LogSkips, infra)
                : outcome;
            record.WallMs = wallMs;
            record.SimSeconds = simSeconds;
            record.Detail = detail;
            if (outcome == OutcomePass && record.Outcome == OutcomeFail)
                record.Detail = $"returned 0 but recorded {record.FailedChecks} failed check(s)";
            else if (outcome == OutcomePass && record.Outcome == OutcomeInfra)
                record.Detail = $"returned 0 but recorded {infra} infrastructure failure(s)";
            _current = null;
            _skipPrefix = null;
            _checkPrefix = null;
            return record;
        }
    }

    // A recorded infrastructure failure outweighs a failed check, because it says the checks could
    // not test what they test.
    internal static string DecideOutcome(bool returnedPass, int checks, int failedChecks, int skips, int infra = 0)
    {
        if (returnedPass && infra > 0)
            return OutcomeInfra;
        if (!returnedPass || failedChecks > 0)
            return OutcomeFail;
        if (checks == 0 && skips > 0)
            return OutcomeSkip;
        return OutcomePass;
    }

    // Called by the watchdog thread: the open entry becomes a timeout, without a sim time, because
    // the universe belongs to the hung test thread.
    public static string? MarkCurrentTimedOut(string detail)
    {
        lock (Gate)
        {
            TestRecord? record = _current;
            if (record == null)
                return null;
            record.Checks = CurrentChecks;
            record.FailedChecks = CurrentFailedChecks;
            record.Skips = Volatile.Read(ref _skips);
            record.LogSkips = Volatile.Read(ref _logSkips);
            record.LogChecks = Volatile.Read(ref _logChecks);
            record.Outcome = OutcomeTimeout;
            record.WallMs = Stopwatch.GetElapsedTime(record.StartTicks).TotalMilliseconds;
            record.Detail = detail;
            _current = null;
            _skipPrefix = null;
            _checkPrefix = null;
            return record.Name;
        }
    }

    public static void Write(int exitCode, string? note)
    {
        lock (Gate)
        {
            WriteFile(ResultsPath, BuildJson(exitCode, note));
            WriteFile(JUnitPath, BuildJUnit(exitCode, note));
        }
    }

    private static void WriteFile(string path, byte[] content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? HarnessLog.DataDirectory);
            File.WriteAllBytes(path, content);
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] results: could not write '{path}': {e.Message}");
        }
    }

    private static string SidePath(string suffix)
    {
        string dir = Path.GetDirectoryName(HarnessLog.FilePath) ?? HarnessLog.DataDirectory;
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(HarnessLog.FilePath)}-{suffix}");
    }

    private static string RunOutcome(int exitCode) => exitCode switch
    {
        0 => OutcomePass,
        1 => OutcomeFail,
        3 => OutcomeTimeout,
        _ => OutcomeInfra,
    };

    private static int Count(string outcome) => Tests.Count(t => t.Outcome == outcome);

    private static byte[] BuildJson(int exitCode, string? note)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", SchemaVersion);
            w.WriteString("runId", Path.GetFileNameWithoutExtension(HarnessLog.FilePath));
            w.WriteString("log", HarnessLog.FilePath);
            w.WriteNumber("exitCode", exitCode);
            w.WriteString("outcome", RunOutcome(exitCode));
            WriteNullable(w, "note", note);
            w.WriteNumber("wallMs", Math.Round(Stopwatch.GetElapsedTime(RunStartTicks).TotalMilliseconds, 1));
            WriteNullable(w, "gameVersion", GameVersion);
            WriteNullable(w, "harnessVersion", HarnessVersion);
            WriteNullable(w, "system", SystemId);
            WriteNullable(w, "profilePath", ProfilePath);
            WriteNullable(w, "instancePath", InstancePath);
            w.WriteStartObject("rules");
            WriteNullable(w, "request", RulesRequest);
            WriteNullable(w, "radiation", Radiation);
            WriteNullable(w, "lifeSupport", LifeSupport);
            w.WriteEndObject();
            WriteNullable(w, "terrainModifiers", TerrainModifiers);
            WriteNullable(w, "seed", Seed);
            WriteNullable(w, "params", Params);
            WriteNullable(w, "testFilter", TestFilter);

            w.WriteStartObject("totals");
            w.WriteNumber("tests", Tests.Count);
            w.WriteNumber(OutcomePass, Count(OutcomePass));
            w.WriteNumber(OutcomeFail, Count(OutcomeFail));
            w.WriteNumber(OutcomeSkip, Count(OutcomeSkip));
            w.WriteNumber(OutcomeInfra, Count(OutcomeInfra));
            w.WriteNumber(OutcomeTimeout, Count(OutcomeTimeout));
            w.WriteNumber("runInfrastructureFailures", RunInfrastructureFailures);
            w.WriteEndObject();

            w.WriteStartArray("optInSkipped");
            foreach (string name in OptInSkipped)
                w.WriteStringValue(name);
            w.WriteEndArray();

            w.WriteStartArray("tests");
            foreach (TestRecord t in Tests)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("suite", t.Suite);
                w.WriteString("outcome", t.Outcome);
                w.WriteNumber("wallMs", Math.Round(t.WallMs, 1));
                WriteNullable(w, "simSeconds", t.SimSeconds);
                WriteNullable(w, "startEpochSeconds", (double?)t.StartEpochSeconds);
                w.WriteString("startEpochNanoseconds", t.StartEpochNanoseconds);
                w.WriteStartObject("checks");
                w.WriteNumber("total", t.Checks);
                w.WriteNumber("failed", t.FailedChecks);
                w.WriteNumber("skipped", t.Skips);
                w.WriteEndObject();
                w.WriteNumber("logSkips", t.LogSkips);
                w.WriteNumber("logChecks", t.LogChecks);
                w.WriteStartArray("leakedVehicles");
                foreach (string id in t.Leaks)
                    w.WriteStringValue(id);
                w.WriteEndArray();
                WriteNullable(w, "detail", t.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value == null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, bool? value)
    {
        if (value == null)
            w.WriteNull(name);
        else
            w.WriteBoolean(name, value.Value);
    }

    // JSON has no NaN or infinity, so a non-finite value is written as null.
    private static void WriteNullable(Utf8JsonWriter w, string name, double? value)
    {
        if (value == null || !double.IsFinite(value.Value))
            w.WriteNull(name);
        else
            w.WriteNumber(name, value.Value);
    }

    private static byte[] BuildJUnit(int exitCode, string? note)
    {
        int failures = Count(OutcomeFail);
        int errors = Count(OutcomeInfra) + Count(OutcomeTimeout);
        int skipped = Count(OutcomeSkip);
        bool runError = RunInfrastructureFailures > 0 || (exitCode >= 2 && errors == 0);
        int total = Tests.Count + (runError ? 1 : 0);
        if (runError)
            errors++;
        string time = Seconds(Stopwatch.GetElapsedTime(RunStartTicks).TotalMilliseconds);

        using MemoryStream stream = new();
        XmlWriterSettings settings = new() { Indent = true, Encoding = new UTF8Encoding(false) };
        using (XmlWriter x = XmlWriter.Create(stream, settings))
        {
            x.WriteStartDocument();
            x.WriteStartElement("testsuites");
            x.WriteAttributeString("name", "HeadlessHarness");
            WriteCounts(x, total, failures, errors, skipped, time);
            x.WriteStartElement("testsuite");
            x.WriteAttributeString("name", "HeadlessHarness");
            WriteCounts(x, total, failures, errors, skipped, time);

            x.WriteStartElement("properties");
            WriteProperty(x, "gameVersion", GameVersion);
            WriteProperty(x, "harnessVersion", HarnessVersion);
            WriteProperty(x, "system", SystemId);
            WriteProperty(x, "profilePath", ProfilePath);
            WriteProperty(x, "rules", RulesRequest);
            WriteProperty(x, "terrainModifiers", TerrainModifiers is bool terrain ? (terrain ? "true" : "false") : null);
            WriteProperty(x, "seed", Seed);
            WriteProperty(x, "params", Params);
            WriteProperty(x, "exitCode", exitCode.ToString(CultureInfo.InvariantCulture));
            x.WriteEndElement();

            foreach (TestRecord t in Tests)
            {
                x.WriteStartElement("testcase");
                x.WriteAttributeString("name", t.Name);
                x.WriteAttributeString("classname", t.Suite);
                x.WriteAttributeString("time", Seconds(t.WallMs));
                switch (t.Outcome)
                {
                    case OutcomeFail:
                        WriteResult(x, "failure", t.Detail ?? "the test returned non-zero", null);
                        break;
                    case OutcomeInfra:
                        WriteResult(x, "error", t.Detail ?? "infrastructure failure", "infrastructure");
                        break;
                    case OutcomeTimeout:
                        WriteResult(x, "error", t.Detail ?? "the watchdog budget ran out", "timeout");
                        break;
                    case OutcomeSkip:
                        x.WriteStartElement("skipped");
                        x.WriteEndElement();
                        break;
                }
                x.WriteEndElement();
            }

            if (runError)
            {
                x.WriteStartElement("testcase");
                x.WriteAttributeString("name", "harness");
                x.WriteAttributeString("classname", "HeadlessHarness.Run");
                x.WriteAttributeString("time", "0");
                WriteResult(x, "error",
                    note ?? $"{RunInfrastructureFailures} infrastructure failure(s) outside any test; see the run log",
                    "infrastructure");
                x.WriteEndElement();
            }

            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndDocument();
        }
        return stream.ToArray();
    }

    private static void WriteCounts(XmlWriter x, int total, int failures, int errors, int skipped, string time)
    {
        x.WriteAttributeString("tests", total.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("failures", failures.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("errors", errors.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("skipped", skipped.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("time", time);
    }

    private static void WriteProperty(XmlWriter x, string name, string? value)
    {
        if (value == null)
            return;
        x.WriteStartElement("property");
        x.WriteAttributeString("name", name);
        x.WriteAttributeString("value", value);
        x.WriteEndElement();
    }

    private static void WriteResult(XmlWriter x, string element, string message, string? type)
    {
        x.WriteStartElement(element);
        x.WriteAttributeString("message", message);
        if (type != null)
            x.WriteAttributeString("type", type);
        x.WriteEndElement();
    }

    private static string Seconds(double ms) => (ms / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
}
