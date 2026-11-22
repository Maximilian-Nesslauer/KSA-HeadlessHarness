# Checks and report files

`HarnessChecks` is the check context a test reports through, and `HarnessData` writes machine-readable output next to the run log. Both are in `HeadlessHarness.Core`.

## HarnessChecks

```csharp
public int Run(HeadlessSession session)
{
    HarnessChecks t = new(Name);
    if (session.System.HomeBody is not IParentBody home)
    {
        t.Skip("the loaded system has no home body");
        return t.Finish();
    }
    t.CheckRel("orbit radius", orbit.SemiMajorAxis, radius, 1e-9);
    t.CheckAbs("eccentricity", orbit.Eccentricity, 0.0, 1e-9);
    t.Check("an engine is fed", TestSupport.AnyActiveEngineFed(vehicle), "after staging");
    t.Info($"mass {vehicle.TotalMass:F0} kg");
    return t.Finish();
}
```

| Member | Effect |
| --- | --- |
| `new HarnessChecks(name)` | a context whose lines start with `[name]`; use the test name |
| `Check(label, pass, detail = null)` | records one check and logs `[name] TEST label: detail => PASS/FAIL`; returns `pass` |
| `CheckRel(label, actual, expected, relTol, floor = 0)` | passes when `|actual - expected| <= relTol * max(|expected|, floor)`; `floor` keeps a zero expectation from demanding bit-exactness |
| `CheckAbs(label, actual, expected, absTol)` | passes when `|actual - expected| <= absTol` |
| `CheckNear(label, actual, expected, absTol, relTol)` | passes when `|actual - expected| <= absTol + relTol * |expected|` |
| `Near(actual, expected, absTol, relTol = 0)`, `NearRel(actual, expected, relTol, floor = 0)` | the same tests as static predicates |
| `Fail(label, detail)` | records a failed check for a precondition the test could not meet |
| `Info(message)` | logs `[name] message`; not a check |
| `Skip(reason)` | counts a skip and logs `[name] SKIP: reason` |
| `Infra(reason)` | the environment cannot test what the test tests (a game state it relies on is not there); logs `[name] INFRA FAIL: reason`, is not a check, and makes `Finish` return 2 |
| `Finish()` | logs `[name] <n> check(s), <f> failed, <s> skipped => PASS/FAIL/SKIP` (with `, <i> infrastructure failure(s) => INFRA FAIL` after `Infra`) and returns 2 after `Infra`, else 1 when a check failed, else 0 |
| `CheckCount`, `FailureCount`, `SkipCount`, `InfrastructureCount`, `Passed` | the counts so far; `Passed` is false after a failed check or `Infra` |

The values a test returns are the constants of `HarnessExit`: `Pass` (0), `TestFailure` (1) and `InfrastructureFailure` (2).

The checked values are logged with round-trip precision (`R`). A NaN never passes a tolerance check, and an infinity passes only against the same infinity.

The counts of every context a test creates add up into that test's entry of the results file (`checks.total`, `checks.failed`, `checks.skipped`). From them the runner decides the outcome:

- the test returned 2: `infra`,
- the test returned another non-zero value, or recorded a failed check: `fail` (a test that returns 0 after a failed check is a test bug, and the runner logs it),
- the test returned 0, recorded no check and reported a skip: `skip`,
- otherwise: `pass`.

A test without a check context reports a skip by logging a line that starts with `[<name>] SKIP` and returning 0; the results count those lines as `logSkips`. A test with a check context of its own that logs in the same format (`[<name>] TEST ... => PASS`) is seen through those lines (`logChecks`), so a test with passed checks and one skipped case still counts as a pass.

## HarnessData

`HarnessData.Create(tag, header)` makes `<run>-<tag>.csv` next to the run log, and `AppendRow(cells...)` appends invariant-culture, CSV-escaped rows (see `ExampleSweepTest`).

`HarnessData.CreateText(tag, extension)` makes a free-form report file `<run>-<tag>.<extension>` (created empty) for output that is not a table: a text report, a JSON dump, a mesh. Write it with `AppendLine`. The tags `results` and `junit` belong to the run's result files and throw `ArgumentException`.

```csharp
HarnessData report = HarnessData.CreateText("my-test-trace", "txt");
report.AppendLine($"t={t:F1} s altitude={alt:F0} m");
```

Both share the run log's basename, so the run script lists them after the run and the log pruning removes them with their run.

## Maintenance on game update

None: the check context and the data files use no game member. The results file is described in [runner.md](runner.md).
