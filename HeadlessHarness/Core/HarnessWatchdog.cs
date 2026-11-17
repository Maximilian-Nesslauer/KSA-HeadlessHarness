using System.Diagnostics;
using System.Globalization;

namespace HeadlessHarness.Core;

// Ends a run that hangs. Two wall-clock budgets: one for the whole run, counted from Start, and one
// for the current phase (a test), counted from EnterPhase. When the earlier one runs out the
// watchdog thread calls the expiry handler with a message that names the phase, and the handler ends
// the process. Wall time here only bounds the run; nothing on the sim path reads it.
internal static class HarnessWatchdog
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static long _runDeadline = long.MaxValue;
    private static long _phaseDeadline = long.MaxValue;
    private static double _runBudgetSeconds;
    private static double _phaseBudgetSeconds;
    private static string _phaseBudgetSource = string.Empty;
    private static string _phase = "start";
    private static Action<string>? _onExpired;

    // runBudgetSeconds null: no run budget, only phase budgets. The thread is a background thread,
    // so it never keeps the process alive.
    public static void Start(double? runBudgetSeconds, Action<string> onExpired)
    {
        lock (Gate)
        {
            if (_thread != null)
                return;
            _onExpired = onExpired;
            if (runBudgetSeconds is double run)
            {
                _runBudgetSeconds = run;
                _runDeadline = Stopwatch.GetTimestamp() + ToTicks(run);
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "HeadlessHarness watchdog" };
            _thread.Start();
        }
    }

    // budgetSeconds null: the phase has no budget of its own, the run budget still applies.
    public static void EnterPhase(string phase, double? budgetSeconds, string budgetSource, Action? begin = null)
    {
        lock (Gate)
        {
            begin?.Invoke();
            _phase = phase;
            _phaseBudgetSeconds = budgetSeconds ?? 0.0;
            _phaseBudgetSource = budgetSource;
            _phaseDeadline = budgetSeconds is double b ? Stopwatch.GetTimestamp() + ToTicks(b) : long.MaxValue;
            Monitor.PulseAll(Gate);
        }
    }

    public static void ExitPhase(string phaseAfter) => EnterPhase(phaseAfter, null, string.Empty);

    // The run is over: no budget fires any more, so the end of a run that finished in time cannot be
    // reported as a timeout.
    public static void Stop()
    {
        lock (Gate)
        {
            _runDeadline = long.MaxValue;
            _phaseDeadline = long.MaxValue;
            _phase = "finished";
            Monitor.PulseAll(Gate);
        }
    }

    private static long ToTicks(double seconds) =>
        (long)Math.Min(seconds * Stopwatch.Frequency, long.MaxValue / 4.0);

    private static void Loop()
    {
        string message;
        lock (Gate)
        {
            while (true)
            {
                long deadline = Math.Min(_runDeadline, _phaseDeadline);
                long now = Stopwatch.GetTimestamp();
                if (deadline != long.MaxValue && now >= deadline)
                    break;
                if (deadline == long.MaxValue)
                {
                    Monitor.Wait(Gate);
                    continue;
                }
                double waitMs = (deadline - now) * 1000.0 / Stopwatch.Frequency;
                Monitor.Wait(Gate, (int)Math.Clamp(Math.Ceiling(waitMs), 1.0, int.MaxValue));
            }

            bool phaseFirst = _phaseDeadline <= _runDeadline;
            message = phaseFirst
                ? $"'{_phase}' is still running after its budget of {Format(_phaseBudgetSeconds)} s ({_phaseBudgetSource})"
                : $"the run budget of {Format(_runBudgetSeconds)} s ran out during '{_phase}'";
            // Block test transitions and normal completion until the expiry handler exits the
            // process, so it cannot label the next test or have its results overwritten.
            _onExpired?.Invoke(message);
        }
    }

    private static string Format(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
