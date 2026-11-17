using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using HeadlessHarness.Core;
using KSA;
using KSA.Kfs;
using KSA.Kfs.Host;
using KSA.Kfs.Host.Services;
using KSA.Kfs.Host.Workbench;

namespace HeadlessHarness.Harness;

// The kFS flight computer prototype (namespace KSA.Kfs) headless: the switch, the output, and loading
// a program onto a vehicle the way the debug console does (kfs load <file> --vehicle, kfs start,
// kfs stop).
//
// kFS needs no renderer. Its vehicle computers run on the solver path (PhysicsBubble.RunFlightComputer
// calls KfsDispatch.TryComputeControl), and SimDriver.Step runs KfsIntegration.BeforeInputEvents where
// Program.PrepareFrame does. A computer ticks on a grid of sim time (VehicleComputer.TryStart aligns
// it to the 10 ms lattice of TickGrid), and the only wall-clock check, the tick deadline of
// KfsVehicleHost.TickDeadlineAt, is off while KfsVehicleHost.FrameBudgetMs is 0. Only
// KfsIntegration.RegisterCommands, which needs a Program, sets a budget, so headless the ticks are
// a function of sim time alone.
public static class Kfs
{
    // "1" turns the prototype on at bring-up. Anything else turns it off, also when the game's own
    // KFS_PROTOTYPE=1 would have turned it on, so a run does not depend on the shell it started from.
    public const string EnvVar = "KSA_HEADLESS_KFS";

    private static int _workspaceCounter;
    private static Action? _releaseDead;

    public static bool Enabled => KfsPrototype.Enabled;

    // The example programs that ship with the game, by workspace path (Examples.KC_ATTITUDE and the
    // other constants of KSA.Kfs.Host.Workbench.Examples name them).
    public static IReadOnlyList<string> ExampleFiles { get; } =
        Examples.Files.Keys.Order(StringComparer.Ordinal).ToList();

    internal static void ConfigureForRun()
    {
        // KfsIntegration.Output prints to Program.ConsoleWindow, and without one to the game log.
        KfsIntegration.Output = new HarnessLogWriter("[kfs] ");
        KfsVehicleHost.FrameBudgetMs = 0.0;

        bool fromGame = KfsPrototype.Enabled;
        bool wanted = Environment.GetEnvironmentVariable(EnvVar) == "1";
        KfsPrototype.Enabled = wanted;
        HarnessLog.Line($"[bringup] kFS prototype {(wanted ? "on" : "off")} (set by {EnvVar}" +
                        (fromGame && !wanted ? "; KFS_PROTOTYPE=1 is ignored headless" : "") +
                        "), tick budget off.");
    }

    // Turns the prototype on or off through the console verbs (kfs on, kfs off), so turning it off
    // hands every vehicle back to the stock flight computer as the console does.
    public static void SetEnabled(bool on)
    {
        if (KfsPrototype.Enabled == on)
            return;
        KfsConsoleCommands.Execute(on ? "on" : "off");
        if (KfsPrototype.Enabled != on)
            throw new InvalidOperationException(
                $"[HeadlessHarness] the console verb 'kfs {(on ? "on" : "off")}' not found - game version may have changed.");
    }

    // Turns the prototype on and returns a scope whose Dispose restores the state it found.
    public static IDisposable Enable()
    {
        bool previous = KfsPrototype.Enabled;
        SetEnabled(true);
        return new RestoreScope(previous);
    }

    // Loads one of the game's example programs (a key of ExampleFiles, or an example name such as
    // "kC_attitude_control") onto the vehicle. Every file of the example's project is written, as
    // kfs examples writes them.
    public static KfsProgram LoadExample(Vehicle vehicle, string example)
    {
        ArgumentNullException.ThrowIfNull(example);
        string file = Examples.Files.ContainsKey(example)
            ? example
            : Examples.MainFile(example) ?? throw new ArgumentException(
                $"'{example}' is no kFS example. Examples: {string.Join(", ", ExampleFiles)}.", nameof(example));
        return Load(vehicle, file, workspace => Examples.Write(workspace, Workspace.FolderOf(file), overwrite: true));
    }

    // Loads a program from source text. file is the workspace path, which picks the language by its
    // extension (.c kC, .kasm kASM) and the project by its folder, for example "test/main.c".
    public static KfsProgram LoadSource(Vehicle vehicle, string file, string source)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(source);
        file = file.Replace('\\', '/');
        // Workbench.Load splits its arguments at spaces, and the workspace resolves a rooted path
        // outside itself.
        if (file.Length == 0 || file.Contains(' ') || Path.IsPathRooted(file))
            throw new ArgumentException($"'{file}' must be a relative workspace path without spaces.", nameof(file));
        return Load(vehicle, file, workspace => workspace.Write(file, source));
    }

    // Drops the hosts of vehicles that are gone (internal KfsVehicleHost.ReleaseDead). Each step with
    // kFS on does this too, so this matters when no step follows a despawn.
    public static void ReleaseDeadHosts()
    {
        _releaseDead ??= AccessTools.Method(typeof(KfsVehicleHost), "ReleaseDead", Type.EmptyTypes)?.CreateDelegate<Action>()
            ?? throw new InvalidOperationException(
                "[HeadlessHarness] KfsVehicleHost.ReleaseDead not found - game version may have changed.");
        _releaseDead();
    }

    // Takes the computer off every vehicle of the loaded system and drops all hosts, so the next test
    // starts with no kFS state. A detached vehicle flies on the stock flight computer at once,
    // without the hand-back a stop runs at the next step.
    public static void DetachAll()
    {
        if (Universe.CurrentSystem is CelestialSystem system)
        {
            for (int i = 0; i < system.Count; i++)
            {
                if (system.GetIndex(i) is Vehicle vehicle)
                    vehicle.KfsHost = null;
            }
        }
        ReleaseDeadHosts();
    }

    private static KfsProgram Load(Vehicle vehicle, string file, Action<Workspace> write)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!KfsPrototype.Enabled)
            throw new InvalidOperationException("kFS is off: call Kfs.Enable or set KSA_HEADLESS_KFS=1 before loading a program.");
        if (vehicle.IsDisposed)
            throw new InvalidOperationException($"vehicle '{vehicle.Id}' is despawned.");

        string folder = Path.Combine(HarnessLog.DataDirectory, "kfs",
            string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-{Interlocked.Increment(ref _workspaceCounter)}"));
        string destination = Path.GetFullPath(Path.Combine(folder, file));
        string workspaceRoot = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(workspaceRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The program path must stay inside its workspace.", nameof(file));
        KfsVehicleHost? hostBefore = vehicle.KfsHost;
        HarnessLogWriter output = new HarnessLogWriter($"[kfs {vehicle.Id}] ");
        // The console's workbench targets Program.ControlledVehicle; this one targets the vehicle.
        Workbench workbench = new Workbench(KfsIntegration.Tables, folder, output)
        {
            ActiveVehicle = () => KfsVehicleHost.For(vehicle),
            ActiveVehicleIdSource = () => vehicle.Id,
        };
        KfsProgram? program = null;
        try
        {
            write(workbench.Workspace);
            if (!workbench.Load(file + " --vehicle"))
            {
                string diagnostics = workbench.LastBuild is { } build
                    ? string.Join("; ", build.Diagnostics.Select(d => d.ToString()))
                    : "nothing was built";
                throw new InvalidOperationException($"kFS could not load '{file}' onto '{vehicle.Id}': {diagnostics}");
            }
            KfsVehicleHost host = KfsVehicleHost.Find(vehicle)
                ?? throw new InvalidOperationException($"kFS loaded '{file}' but '{vehicle.Id}' has no host.");
            program = new KfsProgram(vehicle, host, workbench, file, workbench.LastBuild!, folder);
            return program;
        }
        finally
        {
            if (program == null)
            {
                // A failed load leaves no host behind that the load itself created.
                if (hostBefore == null && vehicle.KfsHost != null)
                {
                    vehicle.KfsHost = null;
                    ReleaseDeadHosts();
                }
                DeleteWorkspace(folder);
            }
        }
    }

    internal static void DeleteWorkspace(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException e)
        {
            HarnessLog.Line($"[kfs] could not delete the workspace '{folder}': {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            HarnessLog.Line($"[kfs] could not delete the workspace '{folder}': {e.Message}");
        }
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly bool _previous;
        private bool _disposed;

        public RestoreScope(bool previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SetEnabled(_previous);
        }
    }

    // Writes whole lines to HarnessLog. kFS writes from the main thread and, through a computer
    // fault, from a solver worker, so the line buffer is locked.
    private sealed class HarnessLogWriter : TextWriter
    {
        private readonly string _prefix;
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();

        public HarnessLogWriter(string prefix) => _prefix = prefix;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                if (value == '\n')
                    FlushLine();
                else if (value != '\r')
                    _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;
            lock (_gate)
            {
                foreach (char c in value)
                {
                    if (c == '\n')
                        FlushLine();
                    else if (c != '\r')
                        _line.Append(c);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                Write(value);
                FlushLine();
            }
        }

        public override void WriteLine() => Write('\n');

        private void FlushLine()
        {
            HarnessLog.Line(_prefix + _line);
            _line.Clear();
        }
    }
}

// A kFS program loaded onto one vehicle. Start and Stop queue the command for the vehicle's computer,
// as the console does, and the computer takes it at the next step. Dispose takes the computer off the
// vehicle at once and deletes the program's workspace.
public sealed class KfsProgram : IDisposable
{
    private readonly string _workspaceFolder;
    private bool _disposed;

    internal KfsProgram(Vehicle vehicle, KfsVehicleHost host, Workbench workbench, string file, AssembleOutcome build, string workspaceFolder)
    {
        Vehicle = vehicle;
        Host = host;
        Workbench = workbench;
        File = file;
        Build = build;
        _workspaceFolder = workspaceFolder;
    }

    public Vehicle Vehicle { get; }

    public KfsVehicleHost Host { get; }

    public Workbench Workbench { get; }

    // The workspace path of the program's main file.
    public string File { get; }

    // The build that was loaded: diagnostics, code size, tick rate and the compile report.
    public AssembleOutcome Build { get; }

    public VehicleComputer Computer => Host.Computer;

    public ComputerState State => Computer.State;

    // True while the program, not the stock flight computer, controls the vehicle.
    public bool UnderKfsControl => Computer.UnderKfsControl;

    // Boots the loaded image at the next step (kfs start).
    public void Start()
    {
        ThrowIfDisposed();
        Workbench.Start();
    }

    // Stops the computer at the next step, which also hands the vehicle back to the stock flight
    // computer (kfs stop).
    public void Stop()
    {
        ThrowIfDisposed();
        Workbench.Stop();
    }

    // The lines the program and its host logged on the computer's console.
    public IReadOnlyList<LogLine> ConsoleLines() => Computer.Console.Snapshot();

    public void LogConsole()
    {
        foreach (LogLine line in ConsoleLines())
            HarnessLog.Line(string.Create(CultureInfo.InvariantCulture, $"[kfs {Vehicle.Id}] k={line.K} t={line.TimeS:F2}s {line.Text}"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ReferenceEquals(Vehicle.KfsHost, Host))
            Vehicle.KfsHost = null;
        Kfs.ReleaseDeadHosts();
        Kfs.DeleteWorkspace(_workspaceFolder);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
