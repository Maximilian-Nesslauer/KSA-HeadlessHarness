namespace HeadlessHarness.Core;

// The values IHarnessTest.Run returns, which are also the exit codes of the run. Any other non-zero
// return of a test counts as a test failure.
public static class HarnessExit
{
    public const int Pass = 0;

    public const int TestFailure = 1;

    // The test found the environment unfit to test what it tests, so its result says nothing about
    // the code under test: the runner records it as an infrastructure failure and the run exits with 2.
    public const int InfrastructureFailure = 2;
}
