using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.ServiceHost;
using Blocker.Sdk.Session;

namespace Blocker.SmokeTest;

// Checks while the session is active (section 4 of the scenario) and after it ended (section 6).
internal sealed partial class SmokeScenario
{
    /// <summary>"About 10 s" for a block, plus the 2 s the program must stay gone to count as blocked.</summary>
    private static readonly TimeSpan BlockWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan BlockLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StayGone = TimeSpan.FromSeconds(2);

    private static readonly string[] ServiceNames = [BlockerServiceNames.Service, BlockerServiceNames.Watchdog];

    private async Task RunEnforcementChecksAsync()
    {
        await StepAsync("status-active", CheckStatusActiveAsync);
        await StepAsync("ifeo-redirects", CheckIfeoRedirects);
        await StepAsync("deny-acl", CheckDenyAcls);
        await StepAsync("service-dacl", CheckServiceDaclRestricted);
        await StepAsync("web-filter", CheckWebFilterAsync);
        _report.Skip("wfp-filters", "not checkable without netsh or a WFP enumeration, and netsh is a blocked bypass tool while the session runs; the service log below shows whether the filters were installed");
        await StepAsync("process-probes", ProbeProcessesAsync);
        await StepAsync("data-root-writes", CheckDataRootWritesAsync);
        _report.Check("allowed-app-still-alive", IsRunning("smoke-allowed"), "the allowlisted probe is still running at the end of the enforcement checks");
    }

    private async Task CheckStatusActiveAsync()
    {
        SessionStatusDto status = await _client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, _cap);
        _report.Check(
            "status-active",
            status.Phase == SessionPhase.Active && status.RemainingSeconds is > 0,
            $"phase={status.Phase}, remaining={status.RemainingSeconds:0}s, cleanupPending={status.CleanupPending}");
    }

    private void CheckIfeoRedirects()
    {
        IReadOnlyList<IfeoEntry> all = SystemState.ReadIfeo();
        IfeoEntry[] owned = [.. all.Where(static e => e.Owner is not null)];
        _report.Info($"{owned.Count(static e => e.View == "64-bit")} image key(s) in the 64-bit view and {owned.Count(static e => e.View == "32-bit")} in the 32-bit view carry the BlockerOwned marker");
        CheckIfeo("ifeo-bypass-tool", "regedit.exe", all);
        CheckIfeo("ifeo-scanned-app", Path.GetFileName(_probeScanned), all);
    }

    private void CheckIfeo(string name, string image, IReadOnlyList<IfeoEntry> all)
    {
        IfeoEntry? native = all.FirstOrDefault(e => e.View == "64-bit" && string.Equals(e.Image, image, StringComparison.OrdinalIgnoreCase));
        IfeoEntry? wow = all.FirstOrDefault(e => e.View == "32-bit" && string.Equals(e.Image, image, StringComparison.OrdinalIgnoreCase));
        bool redirected = native is { Owner: not null } && native.Debugger?.Contains("Blocker.Stub.exe", StringComparison.OrdinalIgnoreCase) == true;
        _report.Check(
            name,
            redirected,
            native is null
                ? $"no IFEO key for {image} in the 64-bit view"
                : $"{image}: Debugger={native.Debugger ?? "(none)"}, BlockerOwned={(native.Owner is null ? "missing" : "set")}; 32-bit view: {(wow is null ? "no key" : wow.Debugger ?? "no Debugger")}");
    }

    private void CheckDenyAcls()
    {
        foreach ((string label, string directory) in new[] { ("install-dir", _paths.InstallDirectory), ("data-root", _paths.DataRoot) })
        {
            RawSecurityDescriptor dacl = SystemState.ReadDirectoryDacl(directory);
            int denies = SystemState.CountServiceDenyAces(dacl);
            _report.Check($"deny-acl-{label}", denies == 2, $"{denies} service deny ACE(s) on {directory} (expected 2, for Administrators and Users)");
            _report.Info($"SDDL as stored: {SystemState.Sddl(dacl)}");
            (bool allowed, string detail) = SystemState.EvaluateSystemWrite(dacl);
            if (label == "data-root")
            {
                _report.Check("system-write-data-root", allowed, $"the DACL as stored gives the service (SYSTEM) {(allowed ? "write" : "NO write")} access to its data root: {detail}");
            }
            else
            {
                _report.Info($"SYSTEM write access to the install directory by this DACL: {(allowed ? "yes" : "no")}, {detail}");
            }
        }
    }

    private void CheckServiceDaclRestricted()
    {
        foreach (string service in ServiceNames)
        {
            RawSecurityDescriptor? dacl = SystemState.ReadServiceSecurity(service);
            _report.Check(
                $"service-dacl-restricted-{service}",
                !SystemState.AdministratorsMayStop(dacl),
                dacl is null ? "no explicit security descriptor: Administrators can still stop the service" : "Administrators may not stop the service");
            _report.Info($"service DACL: {(dacl is null ? "(SCM default)" : SystemState.Sddl(dacl))}");
        }
    }

    private async Task CheckWebFilterAsync()
    {
        string? settings = SystemState.ReadEdgeProxySettings();
        if (settings is null)
        {
            _report.Fail("pac-served", "the Edge ProxySettings policy was not written, so there is no PAC URL to check");
            return;
        }

        using JsonDocument document = JsonDocument.Parse(settings);
        Uri pac = new(document.RootElement.GetProperty("ProxyPacUrl").GetString()!);
        _report.Info($"policy PAC URL: {pac}");

        using HttpClient direct = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using HttpResponseMessage response = await direct.GetAsync(pac, _cap);
        string body = await response.Content.ReadAsStringAsync(_cap);
        bool pacOk = response.StatusCode == HttpStatusCode.OK
            && body.Contains("FindProxyForURL", StringComparison.Ordinal)
            && body.Contains($"PROXY 127.0.0.1:{pac.Port}", StringComparison.Ordinal);
        _report.Check("pac-served", pacOk, $"HTTP {(int)response.StatusCode}, {response.Content.Headers.ContentType?.MediaType}, {body.Length} bytes");

        using HttpClient viaProxy = new(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{pac.Port}"), UseProxy = true, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        using HttpResponseMessage blocked = await viaProxy.GetAsync("http://smoke-not-allowlisted.invalid/", _cap);
        _report.Check("proxy-blocks-unlisted-domain", blocked.StatusCode == HttpStatusCode.Forbidden, $"GET http://smoke-not-allowlisted.invalid/ through the proxy: HTTP {(int)blocked.StatusCode}");

        using HttpResponseMessage allowed = await viaProxy.GetAsync($"http://{Domains[0]}/", _cap);
        _report.Check("proxy-allows-allowlisted-domain", allowed.StatusCode != HttpStatusCode.Forbidden, $"GET http://{Domains[0]}/ through the proxy: HTTP {(int)allowed.StatusCode}");
    }

    private async Task ProbeProcessesAsync()
    {
        if (!_probesReady)
        {
            _report.Skip("process-probes", "the probe setup failed");
            return;
        }

        DateTime launchedAt = DateTime.Now.AddSeconds(-2);
        foreach (string key in new[] { "allowed", "scanned", "unscanned" })
        {
            _launcher.Start(key);
        }

        ProbeWatch allowed = new("smoke-allowed");
        ProbeWatch scanned = new("smoke-blocked");
        ProbeWatch unscanned = new("smoke-unscanned");
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < BlockWindow)
        {
            allowed.Poll(clock.Elapsed);
            scanned.Poll(clock.Elapsed);
            unscanned.Poll(clock.Elapsed);

            // Leave the other tasks a few seconds after the control came up before concluding that they are gone for good.
            bool settled = allowed.FirstSeen is { } up && clock.Elapsed >= up + TimeSpan.FromSeconds(4);
            if (settled && scanned.GoneFor(clock.Elapsed) >= StayGone && unscanned.GoneFor(clock.Elapsed) >= StayGone)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _cap);
        }

        _report.Check(
            "probe-allowed-app",
            allowed.FirstSeen is not null && allowed.Alive && !allowed.DiedAfterSeen,
            allowed.FirstSeen is null
                ? "the allowlisted probe never started (so the two blocked probes below are inconclusive)"
                : $"allowlisted probe started after {allowed.FirstSeen.Value.TotalSeconds:0.0}s and {(allowed.DiedAfterSeen ? "was killed" : "stayed alive")}");
        _report.Info(_launcher.Describe("allowed"));
        EvaluateBlockedProbe("probe-etw-kill", "unscanned", "not in the scan: only the ETW process monitor can stop it", unscanned, allowed, launchedAt);
        EvaluateBlockedProbe("probe-ifeo-block", "scanned", "in the scan: IFEO redirects it to the block screen, the ETW monitor is the backup", scanned, allowed, launchedAt);
    }

    private void EvaluateBlockedProbe(string name, string key, string role, ProbeWatch probe, ProbeWatch control, DateTime launchedAt)
    {
        _report.Info(_launcher.Describe(key));
        if (control.FirstSeen is null)
        {
            _report.Fail(name, $"inconclusive: the control program never started, so launching through the scheduler does not work here ({role})");
        }
        else if (!_launcher.HasRunSince(key, launchedAt))
        {
            _report.Fail(name, $"the scheduler never ran the task ({role})");
        }
        else if (probe.Alive || probe.GoneSince is not { } goneSince)
        {
            _report.Fail(name, $"the program was still running {BlockWindow.TotalSeconds:0} s after the start ({role})");
        }
        else if (goneSince > BlockLimit)
        {
            _report.Fail(name, $"the program was blocked only after {goneSince.TotalSeconds:0.0}s, the limit is {BlockLimit.TotalSeconds:0}s ({role})");
        }
        else
        {
            string seen = probe.LastAlive is { } last
                ? $"seen running until {last.TotalSeconds:0.0}s, then gone"
                : "never observed running (redirected before it started, or killed within one 250 ms poll)";
            _report.Pass(name, $"{seen} ({role})");
        }
    }

    private async Task CheckDataRootWritesAsync()
    {
        // The state machine saves session.bin in the data root on every one-second tick, so a moving timestamp means the service can still write there.
        FileInfo sessionFile = new(_paths.SessionStorePath);
        if (!sessionFile.Exists)
        {
            _report.Fail("service-heartbeat", $"'{sessionFile.FullName}' does not exist while the session is active");
            return;
        }

        DateTime before = sessionFile.LastWriteTimeUtc;
        bool moved = await WaitUntilAsync(
            () =>
            {
                sessionFile.Refresh();
                return sessionFile.LastWriteTimeUtc > before;
            },
            TimeSpan.FromSeconds(10));
        _report.Check("service-heartbeat", moved, moved ? $"session.bin was rewritten by the service (last write {sessionFile.LastWriteTimeUtc:O})" : $"session.bin did not change for 10 s (last write {before:O}); the service cannot write to its data root, or its tick loop is stuck");

        int lines = SystemState.ReadNewestLog(_paths.LogsDirectory).Lines.Count;
        _report.Check("service-log-writes", lines > _logLinesAtStart, $"{lines - _logLinesAtStart} log line(s) written since the session started (the blocked probes are logged at Information)");
    }

    // ---- 6. rollback ----

    private async Task RunRollbackChecksAsync()
    {
        if (_connected)
        {
            await StepAsync("rollback-status", CheckRollbackStatusAsync);
        }

        await StepAsync("rollback-ifeo", CheckRollbackIfeo);
        await StepAsync("rollback-deny-acl", CheckRollbackDenyAcls);
        await StepAsync("rollback-service-dacl", CheckRollbackServiceDacl);
        _report.Check("rollback-browser-policies", SystemState.ReadEdgeProxySettings() is null, "the Edge ProxySettings policy written for the session is gone");
    }

    private async Task CheckRollbackStatusAsync()
    {
        SessionStatusDto status = await _client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, _cap);
        for (Stopwatch wait = Stopwatch.StartNew(); (status.Phase != SessionPhase.Idle || status.CleanupPending) && wait.Elapsed < TimeSpan.FromSeconds(30);)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _cap);
            status = await _client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, _cap);
        }

        _report.Check("rollback-status", status.Phase == SessionPhase.Idle && !status.CleanupPending, $"phase={status.Phase}, cleanupPending={status.CleanupPending} (expected Idle and no cleanup pending)");
    }

    private void CheckRollbackIfeo()
    {
        IfeoEntry[] left = [.. SystemState.ReadIfeo().Where(static e => e.Owner is not null || e.Debugger?.Contains("Blocker.Stub.exe", StringComparison.OrdinalIgnoreCase) == true)];
        _report.Check(
            "rollback-ifeo",
            left.Length == 0,
            left.Length == 0
                ? "no IFEO key carries the BlockerOwned marker or points at the block screen"
                : $"{left.Length} IFEO key(s) left behind: {string.Join(", ", left.Take(10).Select(static e => $"{e.Image} ({e.View})"))}");
    }

    private void CheckRollbackDenyAcls()
    {
        foreach ((string label, string directory) in new[] { ("install-dir", _paths.InstallDirectory), ("data-root", _paths.DataRoot) })
        {
            RawSecurityDescriptor dacl = SystemState.ReadDirectoryDacl(directory);
            int denies = SystemState.CountServiceDenyAces(dacl);
            _report.Check($"rollback-deny-acl-{label}", denies == 0, $"{denies} service deny ACE(s) left on {directory}");
            _report.Info($"SDDL as stored: {SystemState.Sddl(dacl)}");
            if (label == "data-root")
            {
                (bool allowed, string detail) = SystemState.EvaluateSystemWrite(dacl);
                _report.Info($"SYSTEM write access to the data root by this DACL: {(allowed ? "yes" : "NO")}, {detail}");
            }
        }
    }

    private void CheckRollbackServiceDacl()
    {
        foreach (string service in ServiceNames)
        {
            RawSecurityDescriptor? dacl = SystemState.ReadServiceSecurity(service);
            _report.Check($"rollback-service-dacl-{service}", SystemState.AdministratorsMayStop(dacl), "Administrators may stop the service again (normal DACL)");
            _report.Info($"service DACL: {(dacl is null ? "(SCM default)" : SystemState.Sddl(dacl))}");
        }
    }

    /// <summary>Follows one probe program by image name: when it was first and last seen alive and since when it has been gone.</summary>
    private sealed class ProbeWatch(string imageName)
    {
        public bool Alive { get; private set; }

        public TimeSpan? FirstSeen { get; private set; }

        public TimeSpan? LastAlive { get; private set; }

        /// <summary>Gets when the current uninterrupted absence began; <see langword="null"/> while the program is running.</summary>
        public TimeSpan? GoneSince { get; private set; }

        public bool DiedAfterSeen { get; private set; }

        public void Poll(TimeSpan now)
        {
            Alive = IsRunning(imageName);
            if (Alive)
            {
                FirstSeen ??= now;
                LastAlive = now;
                GoneSince = null;
                return;
            }

            GoneSince ??= now;
            DiedAfterSeen |= FirstSeen is not null;
        }

        public TimeSpan GoneFor(TimeSpan now) => GoneSince is { } since ? now - since : TimeSpan.Zero;
    }
}
