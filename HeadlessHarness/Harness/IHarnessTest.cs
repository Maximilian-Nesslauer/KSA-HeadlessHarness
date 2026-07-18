namespace HeadlessHarness.Harness;

// A pluggable test run by the harness against a live, GPU-free session. A consumer mod (a test
// project referencing HeadlessHarness) implements this on a public class with a parameterless
// constructor, and the harness discovers and runs it after bring-up. Return 0 on pass, non-zero on
// failure. Keep Name stable and unique; tests run ordered by Name.
public interface IHarnessTest
{
    string Name { get; }

    int Run(HeadlessSession session);
}
