using System.Diagnostics;
using System.Security.Principal;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Policy;
using Blocker.Core.ServiceHost;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocker.SmokeTest;

/// <summary>
/// The one scenario: status, allowlist, a real session, enforcement checks, stop, rollback checks, service log. Steps that depend on an earlier
/// success are skipped, everything else keeps going after a failure so one run shows the whole picture. This file holds the flow, the setup
/// and the session start/stop; the enforcement and rollback checks are in <c>EnforcementChecks.cs</c>.
/// </summary>
internal sealed partial class SmokeScenario
{
    // The state machine refuses anything shorter (SessionConfigValidator.MinDuration), so a "short" session is 15 minutes that this tool ends itself.
    private const int SessionMinutes = 15;
    private const string ProbeArguments = "-t 127.0.0.1";

    private static readonly string[] Domains = ["example.com"];

    private readonly StepReport _report;
    private readonly CancellationToken _cap;
    private readonly BlockerPaths _paths;
    private readonly IpcClient _client;
    private readonly ScheduledLauncher _launcher = new();
    private readonly string _probeAllowed = Path.Combine(Path.GetTempPath(), "smoke-allowed.exe");
    private readonly string _probeScanned = Path.Combine(Path.GetTempPath(), "smoke-blocked.exe");
    private readonly string _probeUnscanned = Path.Combine(Path.GetTempPath(), "smoke-unscanned.exe");
    private List<AllowlistEntryDto> _apps = [];
    private bool _connected;
    private bool _probesReady;
    private bool _sessionStarted;
    private string? _breakGlassCode;
    private int _logLinesAtStart;

    public SmokeScenario(StepReport report, CancellationToken cap)
    {
        _report = report;
        _cap = cap;
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsDetox");
        _paths = BlockerPaths.Create(programData, installDirectory);
        _client = new IpcClient(
            IpcPipeNames.Service,
            NullLogger<IpcClient>.Instance,
            new IpcClientOptions { RequestTimeout = TimeSpan.FromMinutes(2.5) });
    }

    /// <summary>Runs every step; failures are recorded in the <see cref="StepReport"/>.</summary>
    public async Task RunAsync()
    {
        try
        {
            await StepAsync("elevated-administrator", CheckElevation);
            await StepAsync("service-status", CheckServiceStatusAsync);
            await StepAsync("service-version", CheckServiceVersion);
            if (_connected)
            {
                await StepAsync("plugin-list", CheckPluginListAsync);
                await StepAsync("probe-setup", PrepareProbes);
                await StepAsync("allowlist-scan", ScanAppsAsync);
                await StepAsync("allowlist-set", SetAllowlistAsync);
                await StepAsync("session-start", StartSessionAsync);
                if (_sessionStarted)
                {
                    await RunEnforcementChecksAsync();
                    await StepAsync("session-stop", StopSessionAsync);
                }
                else
                {
                    _report.Skip("enforcement-checks", "the session did not start (see session-start); the rollback checks below still run");
                }
            }
            else
            {
                _report.Skip("session-steps", "the service is not reachable; only the state checks below run");
            }

            await RunRollbackChecksAsync();
        }
        catch (OperationCanceledException) when (_cap.IsCancellationRequested)
        {
            _report.Fail("overall-cap", "the overall time cap was reached; the remaining steps were skipped");
        }
    }

    /// <summary>Always runs, even after a crash or the cap: ends a session that is still running, removes the probes and closes the pipe.</summary>
    public async Task FinishAsync()
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(60));
        await EnsureSessionEndedAsync(cleanup.Token);
        foreach (string name in new[] { "smoke-allowed", "smoke-blocked", "smoke-unscanned" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                Terminate(process);
            }
        }

        _launcher.Dispose();
        foreach (string probe in new[] { _probeAllowed, _probeScanned, _probeUnscanned })
        {
            DeleteBestEffort(probe);
        }

        await _client.DisposeAsync();
    }

    /// <summary>Kills and disposes <paramref name="process"/>; one that is already gone is fine.</summary>
    private static void Terminate(Process process)
    {
        using (process)
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"     process {process.Id} was already gone: {ex.Message}");
            }
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"     could not delete '{path}': {ex.Message}");
        }
    }

    private Task StepAsync(string name, Func<Task> step) => _report.RunAsync(name, step, _cap);

    private Task StepAsync(string name, Action step) => _report.RunAsync(name, () =>
    {
        step();
        return Task.CompletedTask;
    }, _cap);

    private void CheckElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        _report.Check("elevated-administrator", administrator, $"{identity.Name}, elevated administrator: {administrator} (session.start and the registry/ACL reads need it)");
    }

    // ---- 1. service reachable ----

    private async Task CheckServiceStatusAsync()
    {
        Exception? last = null;
        for (DateTime deadline = DateTime.UtcNow.AddSeconds(30); DateTime.UtcNow < deadline && !_connected;)
        {
            try
            {
                await _client.ConnectAsync(_cap);
                _connected = true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), _cap);
            }
        }

        if (!_connected)
        {
            _report.Fail("service-status", $"cannot connect to \\\\.\\pipe\\{IpcPipeNames.Service}: {last}");
            return;
        }

        SessionStatusDto status = await _client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, _cap);
        _report.Check(
            "service-status",
            status.Phase == SessionPhase.Idle && !status.CleanupPending,
            $"phase={status.Phase}, cleanupPending={status.CleanupPending}, emergencyPassesPerDay={status.EmergencyPassesPerDay}, passesLeftToday={status.EmergencyPassesLeftToday} (expected Idle, no cleanup pending)");
    }

    private void CheckServiceVersion()
    {
        // The IPC contract has no version call; the installed binaries are the source of truth.
        if (!File.Exists(_paths.ServiceExecutablePath))
        {
            _report.Fail("service-version", $"'{_paths.ServiceExecutablePath}' does not exist");
            return;
        }

        FileVersionInfo service = FileVersionInfo.GetVersionInfo(_paths.ServiceExecutablePath);
        _report.Pass("service-version", $"Blocker.Service.exe {service.ProductVersion ?? service.FileVersion}");
    }

    private async Task CheckPluginListAsync()
    {
        ListPluginsResponse plugins = await _client.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, _cap);
        string[] faulted = [.. plugins.Plugins.Where(static p => p.Faulted).Select(static p => p.Id)];
        string list = string.Join(", ", plugins.Plugins.Select(static p => $"{p.Id} {p.Version}{(p.Enabled ? string.Empty : " (disabled)")}"));
        _report.Check("plugin-list", faulted.Length == 0, $"{plugins.Plugins.Count} plugin(s): {list}{(faulted.Length == 0 ? string.Empty : $"; faulted: {string.Join(", ", faulted)}")}");
    }

    // ---- 2. allowlist ----

    private void PrepareProbes()
    {
        string ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        foreach (string probe in new[] { _probeAllowed, _probeScanned, _probeUnscanned })
        {
            File.Copy(ping, probe, overwrite: true);
        }

        _launcher.Register("allowed", _probeAllowed, ProbeArguments);
        _launcher.Register("scanned", _probeScanned, ProbeArguments);
        _launcher.Register("unscanned", _probeUnscanned, ProbeArguments);
        _probesReady = true;
        _report.Pass("probe-setup", $"copies of ping.exe at {_probeAllowed}, {_probeScanned}, {_probeUnscanned}; three Task Scheduler tasks registered to start them outside this tool's process tree");
    }

    private async Task ScanAppsAsync()
    {
        // The scan lists running processes, which is how a program outside the Start Menu gets into the inventory that plans the IFEO redirects.
        ProcessStartInfo start = new(_probeScanned, ProbeArguments) { UseShellExecute = false, CreateNoWindow = true };
        Process? probe = Process.Start(start);
        try
        {
            await WaitUntilAsync(() => IsRunning("smoke-blocked"), TimeSpan.FromSeconds(5));
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(_cap);
            limit.CancelAfter(TimeSpan.FromSeconds(100));
            ScanAppsResponse scan = await _client.InvokeAsync<ScanAppsResponse>(IpcMethods.AllowlistScanApps, limit.Token);
            InstalledAppDto? found = scan.Apps.FirstOrDefault(a => string.Equals(a.ExecutablePath, _probeScanned, StringComparison.OrdinalIgnoreCase));
            _report.Check(
                "allowlist-scan",
                found is not null,
                found is null
                    ? $"{scan.Apps.Count} apps scanned but the running probe '{_probeScanned}' is not among them, so the IFEO redirect of a scanned app cannot be verified"
                    : $"{scan.Apps.Count} apps scanned; the running probe is listed (sources: {found.Sources})");
        }
        finally
        {
            if (probe is not null)
            {
                Terminate(probe);
            }
        }
    }

    private async Task SetAllowlistAsync()
    {
        List<AllowlistEntryDto> apps =
        [
            new("Smoke allowed probe", _probeAllowed, null, null),
            new("Notepad", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), null, null),
        ];
        if (Environment.ProcessPath is { } self)
        {
            apps.Add(new AllowlistEntryDto("WindowsDetox smoke test", self, null, null));
        }

        apps.AddRange(AncestorEntries());
        _apps = [.. apps.DistinctBy(static a => a.ExecutablePath, StringComparer.OrdinalIgnoreCase)];

        AllowlistDto allowlist = new(_apps, Domains);
        OperationResponse set = await _client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, allowlist, _cap);
        _report.Check("allowlist-set", set.Success, set.Success ? $"{_apps.Count} apps and domain(s) {string.Join(", ", Domains)} saved" : string.Join(" | ", set.Errors));

        AllowlistDto stored = await _client.InvokeAsync<AllowlistDto>(IpcMethods.AllowlistGet, _cap);
        bool same = stored.Apps.Count == _apps.Count && stored.Domains.SequenceEqual(Domains, StringComparer.OrdinalIgnoreCase);
        _report.Check("allowlist-get", same, $"stored {stored.Apps.Count} apps and {stored.Domains.Count} domain(s); sent {_apps.Count} and {Domains.Length}");
    }

    /// <summary>The shell and CI runner above this tool must stay alive, or the first kill of the session ends the CI step.</summary>
    private IEnumerable<AllowlistEntryDto> AncestorEntries()
    {
        BypassTools bypass = new();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        foreach (Ancestor ancestor in ProcessAncestry.OfCurrentProcess())
        {
            string name = ancestor.ImagePath is null ? "?" : Path.GetFileName(ancestor.ImagePath);
            if (ancestor.ImagePath is null)
            {
                _report.Info($"ancestor pid {ancestor.ProcessId}: image unknown, not allowlisted");
            }
            else if (bypass.IsBypassTool(name, null))
            {
                _report.Info($"WARNING ancestor {name} (pid {ancestor.ProcessId}) is a bypass tool and is always blocked: the session will kill it. Run this tool from a shell that is not cmd/powershell/pwsh");
            }
            else if (ancestor.ImagePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || CriticalProcesses.IsKnownImageName(name))
            {
                _report.Info($"ancestor {name} (pid {ancestor.ProcessId}): Windows binary, never needs the allowlist");
            }
            else
            {
                _report.Info($"ancestor {name} (pid {ancestor.ProcessId}) allowlisted: {ancestor.ImagePath}");
                yield return new AllowlistEntryDto($"CI ancestor {name}", ancestor.ImagePath, null, null);
            }
        }
    }

    // ---- 3. session ----

    private async Task StartSessionAsync()
    {
        StartSessionRequest request = new(SessionMinutes, 1, 1, false, _apps, Domains);
        StartSessionResponse response;
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(_cap);
            limit.CancelAfter(TimeSpan.FromMinutes(2.5));
            response = await _client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, request, limit.Token);
        }
        catch (IpcException ex)
        {
            _report.Fail("session-start", $"the service answered with error {ex.Code}: {ex.Message}");
            return;
        }

        _report.Info($"session.start result: Success={response.Success}, SessionId={response.SessionId}, BreakGlassCode={(response.BreakGlassCode is null ? "none" : $"received ({response.BreakGlassCode.Length} chars, kept in memory only)")}");
        foreach (string error in response.Errors)
        {
            _report.Info($"session.start error: {error}");
        }

        _sessionStarted = response.Success;
        _breakGlassCode = response.BreakGlassCode;
        _logLinesAtStart = SystemState.ReadNewestLog(_paths.LogsDirectory).Lines.Count;
        _report.Check(
            "session-start",
            response.Success,
            response.Success ? $"session {response.SessionId} started for {SessionMinutes} minutes" : string.Join(" | ", response.Errors));
    }

    private async Task StopSessionAsync()
    {
        // A Release service has no developer abort and an emergency pass only suspends enforcement, so the break-glass code is the way out.
        if (_breakGlassCode is null)
        {
            _report.Fail("session-stop", "the service returned no break-glass code, so the session cannot be ended from here");
            return;
        }

        BreakGlassResponse response = await _client.InvokeAsync<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, new BreakGlassRequest(_breakGlassCode), _cap);
        _report.Check("session-stop", response.Success, response.Success ? "ended with the break-glass code" : "the service rejected the break-glass code");
        if (response.Success)
        {
            _sessionStarted = false;
        }
    }

    private async Task EnsureSessionEndedAsync(CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            SessionStatusDto status = await _client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, cancellationToken);
            if (status.Phase == SessionPhase.Idle)
            {
                return;
            }

            if (_breakGlassCode is null)
            {
                _report.Fail("session-cleanup", $"a session is still running ({status.Phase}, {status.RemainingSeconds:0}s left) and no break-glass code is known; it ends by itself");
                return;
            }

            BreakGlassResponse response = await _client.InvokeAsync<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, new BreakGlassRequest(_breakGlassCode), cancellationToken);
            _report.Info($"session-cleanup: the session was still running at exit; break-glass accepted: {response.Success}");
        }
        catch (Exception ex) when (ex is IpcException or IOException or TimeoutException or OperationCanceledException)
        {
            _report.Fail("session-cleanup", $"could not make sure the session is over: {ex.Message}");
        }
    }

    // ---- 7. diagnostics ----

    /// <summary>Prints the tail of the newest service log; the last thing the tool does, whatever happened before.</summary>
    public void PrintServiceLog()
    {
        Console.WriteLine();
        Console.WriteLine("===== service log (newest file, last 150 lines) =====");
        try
        {
            (string? path, IReadOnlyList<string> lines) = SystemState.ReadNewestLog(_paths.LogsDirectory);
            Console.WriteLine(path is null ? $"(no service-*.log in '{_paths.LogsDirectory}')" : $"{path} ({lines.Count} lines in total)");
            foreach (string line in lines.Skip(Math.Max(0, lines.Count - 150)))
            {
                Console.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"(the log could not be read: {ex.Message})");
        }

        Console.WriteLine("===== end of service log =====");
    }

    // ---- helpers ----

    private static bool IsRunning(string imageNameWithoutExtension)
    {
        Process[] processes = Process.GetProcessesByName(imageNameWithoutExtension);
        try
        {
            return processes.Any(static p => !p.HasExited);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        for (Stopwatch watch = Stopwatch.StartNew(); watch.Elapsed < timeout;)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _cap);
        }

        return condition();
    }
}
