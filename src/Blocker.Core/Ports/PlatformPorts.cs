using Blocker.Sdk.Model;

namespace Blocker.Core.Ports;

/// <summary>Real-time process start/stop notifications (ETW Microsoft-Windows-Kernel-Process on Windows).</summary>
public interface IProcessMonitor : IAsyncDisposable
{
    /// <summary>Raised for every new process (all sessions). Handlers must be fast; heavy work goes to a queue.</summary>
    event EventHandler<ProcessStartedEventArgs>? ProcessStarted;

    /// <summary>Raised when a process exits.</summary>
    event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    /// <summary>Starts monitoring.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Snapshot of processes running right now (used at session start and to seed the process tree).</summary>
    IReadOnlyList<ProcessInfo> EnumerateRunning();
}

/// <summary>Minimal process facts known at creation time.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="ParentProcessId">Parent process id as the process claims it. A process can choose its parent (<c>PROC_THREAD_ATTRIBUTE_PARENT_PROCESS</c>), so never trust it for inheritance when <paramref name="CreatorProcessId"/> is known.</param>
/// <param name="SessionId">Terminal-services session id (0 = services).</param>
/// <param name="ImagePath">Full image path (may be empty if not resolvable).</param>
/// <param name="StartTimeTicks">Process creation time as FILETIME ticks (identity together with the pid).</param>
/// <param name="CreatorProcessId">
/// The process that really created this one (the process id in the header of the creation event), or <see langword="null"/> when the source cannot
/// tell, as for a snapshot of running processes. Unlike the parent it cannot be chosen by the new process.
/// </param>
public sealed record ProcessInfo(int ProcessId, int ParentProcessId, int SessionId, string ImagePath, long StartTimeTicks, int? CreatorProcessId = null);

/// <summary>Process started.</summary>
public sealed class ProcessStartedEventArgs(ProcessInfo process) : EventArgs
{
    /// <summary>The new process.</summary>
    public ProcessInfo Process { get; } = process;
}

/// <summary>Process exited.</summary>
public sealed class ProcessExitedEventArgs(int processId, long startTimeTicks) : EventArgs
{
    /// <summary>Process id.</summary>
    public int ProcessId { get; } = processId;

    /// <summary>Creation time ticks of the exited process.</summary>
    public long StartTimeTicks { get; } = startTimeTicks;
}

/// <summary>Process control operations. Implementations must refuse critical processes themselves as a second line of defence.</summary>
public interface IProcessController
{
    /// <summary>Terminates a process if (pid, start time) still identifies it. Returns false if it already exited or is protected.</summary>
    bool TryTerminate(int processId, long startTimeTicks);

    /// <summary>Asks the process' top-level windows to close (WM_CLOSE) — performed by the user-session agent.</summary>
    Task<bool> TryCloseGracefullyAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Package family name of a packaged (Store) process, or null.</summary>
    string? GetPackageFamilyName(int processId);
}

/// <summary>Launches executables inside the active interactive user session (from Session 0).</summary>
public interface IUserSessionLauncher
{
    /// <summary>Starts <paramref name="executablePath"/> with <paramref name="arguments"/> as the logged-on console user. Returns the pid or null when no user is logged on.</summary>
    int? LaunchInActiveSession(string executablePath, string arguments);
}

/// <summary>Launches the plugin host with a restricted (low-integrity / AppContainer) token.</summary>
public interface IRestrictedProcessLauncher
{
    /// <summary>Starts the process restricted and returns a handle to wait on / kill.</summary>
    IRestrictedProcess Launch(string executablePath, string arguments);
}

/// <summary>A launched restricted process.</summary>
public interface IRestrictedProcess : IDisposable
{
    /// <summary>Process id.</summary>
    int ProcessId { get; }

    /// <summary>Completes when the process exits.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process tree.</summary>
    void Kill();

    /// <summary>Gets the exit code once the process has exited; <see langword="null"/> while it runs or when unknown.</summary>
    int? ExitCode => null;
}

/// <summary>Authenticode verification.</summary>
public interface ISignatureVerifier
{
    /// <summary>True when the file has a valid embedded or catalog signature chaining to Microsoft.</summary>
    bool IsMicrosoftSigned(string path);
}

/// <summary>Discovers installed applications for the allowlist picker.</summary>
public interface IAppInventoryScanner
{
    /// <summary>Scans Start Menu shortcuts, Uninstall registry keys, Store packages and running processes.</summary>
    Task<IReadOnlyList<InstalledApp>> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>An application candidate for the allowlist.</summary>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="ExecutablePath">Main executable (null for Store apps without a resolvable exe).</param>
/// <param name="Sha256">Hash of the executable (lower-case hex), when computed.</param>
/// <param name="PackageFamilyName">Store package family name, or null.</param>
/// <param name="IconPng">PNG bytes of the icon (32 px), or null.</param>
/// <param name="Sources">Where it was found.</param>
public sealed record InstalledApp(string DisplayName, string? ExecutablePath, string? Sha256, string? PackageFamilyName, byte[]? IconPng, AppSource Sources);

/// <summary>Discovery sources.</summary>
[Flags]
public enum AppSource
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Start Menu shortcut.</summary>
    StartMenu = 1,

    /// <summary>Uninstall registry key.</summary>
    Registry = 2,

    /// <summary>Microsoft Store package.</summary>
    StorePackage = 4,

    /// <summary>Currently running process.</summary>
    Running = 8,
}

/// <summary>Kernel-level network enforcement for browsers during a session (WFP dynamic session on Windows).</summary>
public interface IBrowserFirewall : IAsyncDisposable
{
    /// <summary>Installs filters: for each browser executable block direct TCP 80/443 except loopback, UDP 443 (QUIC) and TCP/UDP 853 (DoT).
    /// Filters live in a dynamic session and vanish when the process exits.</summary>
    Task EnableAsync(IReadOnlyList<string> browserExecutables, CancellationToken cancellationToken);

    /// <summary>Removes all filters.</summary>
    Task DisableAsync(CancellationToken cancellationToken);

    /// <summary>Whether filters are installed.</summary>
    bool IsEnabled { get; }
}

/// <summary>Installs/removes Windows services via the SCM (never sc.exe).</summary>
public interface IServiceInstaller
{
    /// <summary>Creates or updates the service with failure actions "restart after 0 ms".</summary>
    void InstallOrUpdate(string serviceName, string displayName, string executablePath, string arguments);

    /// <summary>Stops and deletes the service.</summary>
    void Uninstall(string serviceName);

    /// <summary>Starts the service if stopped.</summary>
    void EnsureStarted(string serviceName);
}
