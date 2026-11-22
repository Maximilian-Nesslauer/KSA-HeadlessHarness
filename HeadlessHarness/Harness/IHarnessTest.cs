namespace HeadlessHarness.Harness;

// A pluggable test run by the harness against a live, GPU-free session. A consumer mod (a test
// project referencing HeadlessHarness) implements this on a public class with a parameterless
// constructor, and the harness discovers and runs it after bring-up. Return 0 on pass, 2
// (HarnessExit.InfrastructureFailure) when the environment cannot test what the test tests, and any
// other non-zero value on failure. Keep Name stable and unique; tests run ordered by Name. A test that cannot run here
// returns 0 after it reports a skip, through HarnessChecks.Skip or with a log line that starts with
// "[<Name>] SKIP"; the results file then lists it as skipped when it recorded no check.
public interface IHarnessTest
{
    string Name { get; }

    // Opt-in tests are skipped by a default run and execute only when KSA_HEADLESS_TESTS names them.
    // Set it on tests too expensive for the normal suite (parameter sweeps, soak runs), so they can
    // stay deployed without lengthening every run.
    bool OptIn => false;

    // The wall-clock budget of one Run in seconds. When it runs out the harness logs that this test
    // hung, writes the results file and ends the process with exit code 3. Null takes the run's
    // default from KSA_HEADLESS_TEST_TIMEOUT, which is unset (no budget) unless the run sets it.
    int? TimeoutSeconds => null;

    int Run(HeadlessSession session);
}
