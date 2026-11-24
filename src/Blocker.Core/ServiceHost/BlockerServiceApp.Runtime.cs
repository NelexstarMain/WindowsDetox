using System.IO.Abstractions;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Orchestration;
using Blocker.Core.Plugins;
using Blocker.Core.Ports;
using Blocker.Core.Security;
using Blocker.Core.Supervision;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.ServiceHost;

// Recovery, background loops and the status synchronization of the running service.
public sealed partial class BlockerServiceApp
{
    private void OnNotification(object? sender, EnforcementNotification notification) => _notifications.Enqueue(notification);

    private async ValueTask<IReadOnlyList<InterceptDecision>> InterceptProcessAsync(ProcessDescriptor process, CancellationToken cancellationToken)
    {
        InterceptResponse response = await _gateway.InterceptProcessAsync(process, cancellationToken).ConfigureAwait(false);
        return response.Block
            ? [InterceptDecision.Block(string.IsNullOrWhiteSpace(response.Reason) ? "Blocked by a plugin." : response.Reason)]
            : [];
    }

    /// <summary>
    /// Restores a persisted session. A session file that fails verification cannot be resumed (its break-glass verifier is in it): it is
    /// removed and the leftovers of the journal are rolled back, so a damaged file never leaves the machine locked with nothing to unlock it.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Coordinator.RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            LogSessionStoreDamaged(_logger, ex);
            await _sessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            await Coordinator.RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Brings every consumer of the session state up to date: pushes the status to subscribed clients, tells the plugin host, writes the
    /// supervision file and makes sure the watchdog runs while a session does. Safe to call from anywhere; calls are serialized.
    /// </summary>
    internal async ValueTask SyncStatusAsync(CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSnapshot snapshot = Coordinator.Snapshot;
            var status = SessionStatusDto.From(snapshot, Coordinator.CleanupPending);
            if (status != _lastPublished)
            {
                _lastPublished = status;
                await Server.PublishAsync(IpcMethods.EventSessionChanged, status, cancellationToken).ConfigureAwait(false);
            }

            await SyncSupervisionAsync(snapshot, cancellationToken).ConfigureAwait(false);
            (SessionPhase, Guid?, int) hostState = (snapshot.Phase, snapshot.SessionId, snapshot.EmergencyPassesLeftToday);
            if (_lastHostState != hostState)
            {
                _lastHostState = hostState;
                await _gateway.NotifySessionChangedAsync(snapshot, VerifiedOrSystemNow(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSyncFailed(_logger, ex);
        }
        finally
        {
            _ = _syncGate.Release();
        }
    }

    private DateTimeOffset VerifiedOrSystemNow() => _verified.TryGetUtcNow(out DateTimeOffset utc) ? utc : _ports.Time.GetUtcNow();

    private async ValueTask SyncSupervisionAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        bool active = snapshot.IsSessionRunning;
        if (_lastActive != active || _lastSessionId != snapshot.SessionId)
        {
            try
            {
                await _supervisionFile.WriteAsync(new SupervisionStatus(active, snapshot.SessionId, _ports.Time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                (_lastActive, _lastSessionId, _watchdogEnsured) = (active, snapshot.SessionId, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSupervisionWriteFailed(_logger, ex); // retried by the next sync: _lastActive was not updated
            }
        }

        if (active && !_watchdogEnsured && _ports.ServiceInstaller is { } installer)
        {
            try
            {
                installer.EnsureStarted(BlockerServiceNames.Watchdog);
                _watchdogEnsured = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _watchdogEnsured = true; // one warning per session, not one per second: the mutual supervisor keeps trying
                LogWatchdogStartFailed(_logger, ex);
            }
        }
    }

    private async Task RunGuardedAsync(string name, Func<CancellationToken, Task> body)
    {
        try
        {
            await body(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            LogLoopStopped(_logger, name);
        }
        catch (Exception ex)
        {
            LogBackgroundFailed(_logger, ex, name);
        }
    }

    private async Task RefreshTimeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_verified.HasAnchor ? TimeRefreshInterval : TimeRetryInterval, _ports.Time, cancellationToken).ConfigureAwait(false);
            _ = await _verified.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StatusLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StatusInterval, _ports.Time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await SyncStatusAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Installs each package in the install directory's <c>bundled-plugins</c> folder once, through the normal pipeline (evaluation in the
    /// restricted host). Offered packages are remembered, so a plugin the user removed stays removed. Waits while a session runs.
    /// </summary>
    private async Task InstallBundledPluginsAsync(CancellationToken cancellationToken)
    {
        IFileSystem fs = _ports.FileSystem;
        if (!fs.Directory.Exists(_paths.BundledPluginsDirectory))
        {
            return;
        }

        HashSet<string> offered = fs.File.Exists(_paths.BundledPluginsMarkerPath)
            ? [.. await fs.File.ReadAllLinesAsync(_paths.BundledPluginsMarkerPath, cancellationToken).ConfigureAwait(false)]
            : [];
        foreach (string path in fs.Directory.GetFiles(_paths.BundledPluginsDirectory, "*.zip").Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = fs.Path.GetFileName(path);
            if (offered.Contains(name))
            {
                await UpgradeBundledPluginAsync(path, name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            byte[] package = await fs.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            for (int attempt = 1; ; attempt++)
            {
                while (Coordinator.IsSessionActive)
                {
                    await Task.Delay(BundledRetryDelay, _ports.Time, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    InstallPluginResponse result = await _handlers.InstallBundledAsync(package, cancellationToken).ConfigureAwait(false);
                    LogBundledPlugin(_logger, name, result.Success, string.Join(" | ", result.Errors));
                    if (!result.Success && attempt < BundledAttempts)
                    {
                        // Most often the plugin host is not ready yet: wait and try again.
                        await Task.Delay(BundledRetryDelay, _ports.Time, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (result.Success)
                    {
                        offered.Add(name);
                        await fs.File.AppendAllLinesAsync(_paths.BundledPluginsMarkerPath, [name], cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < BundledAttempts)
                {
                    // The plugin host may still be starting: try again shortly.
                    LogBundledPluginRetry(_logger, ex, name, attempt);
                    await Task.Delay(BundledRetryDelay, _ports.Time, cancellationToken).ConfigureAwait(false);
                }
            }

        }
    }

    /// <summary>
    /// A bundled package that was offered before is not offered again, so a fixed or extended bundled plugin would never reach an existing
    /// install. When the plugin is still installed and enabled and the shipped package has a higher version, install it over the old one
    /// (through the same pipeline, so evaluation, lock votes and "no session" apply). A removed or disabled plugin is left alone.
    /// </summary>
    private async Task UpgradeBundledPluginAsync(string path, string name, CancellationToken cancellationToken)
    {
        IFileSystem fs = _ports.FileSystem;
        byte[] package = await fs.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        for (int attempt = 1; attempt <= BundledAttempts; attempt++)
        {
            try
            {
                using var stream = new MemoryStream(package, writable: false);
                PluginPackageReadResult read = await PluginPackageReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (read.Package?.Manifest is not { } manifest
                    || await _handlers.ResolvePluginAsync(manifest.Id.Value, cancellationToken).ConfigureAwait(false) is not { } installed
                    || !Version.TryParse(installed.Version, out Version? have)
                    || have >= manifest.Version)
                {
                    return;
                }

                while (Coordinator.IsSessionActive)
                {
                    await Task.Delay(BundledRetryDelay, _ports.Time, cancellationToken).ConfigureAwait(false);
                }

                InstallPluginResponse result = await _handlers.InstallBundledAsync(package, cancellationToken).ConfigureAwait(false);
                LogBundledUpgrade(_logger, name, have.ToString(), manifest.Version.ToString(), result.Success, string.Join(" | ", result.Errors));
                if (result.Success)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < BundledAttempts)
            {
                LogBundledPluginRetry(_logger, ex, name, attempt);
            }

            await Task.Delay(BundledRetryDelay, _ports.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InstalledApp> apps = await _ports.AppScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        if (await Coordinator.UpdateScannedAppsAsync(apps, cancellationToken).ConfigureAwait(false))
        {
            await _scanCache.SaveAsync(apps, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocker service started (session phase {Phase}).")]
    private static partial void LogStarted(ILogger logger, SessionPhase phase);

    [LoggerMessage(Level = LogLevel.Error, Message = "The app rules or their usage could not be read; the service continues with the rules it has (none) and no usage.")]
    private static partial void LogAppRulesLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The app rule enforcer could not complete its start; the service continues and the next periodic check tries again.")]
    private static partial void LogAppEnforcerStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocker service stopped; an active session stays in force.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The session file failed verification and was discarded; leftover protections are rolled back.")]
    private static partial void LogSessionStoreDamaged(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Synchronizing the session status failed.")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the supervision status failed; retrying.")]
    private static partial void LogSupervisionWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting the watchdog service failed.")]
    private static partial void LogWatchdogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundled plugin {Package}: installed={Success} {Errors}")]
    private static partial void LogBundledPlugin(ILogger logger, string package, bool success, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bundled plugin {Package}: upgrade {From} to {To}: installed={Success} {Errors}")]
    private static partial void LogBundledUpgrade(ILogger logger, string package, string from, string to, bool success, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installing bundled plugin {Package} failed (attempt {Attempt}); retrying.")]
    private static partial void LogBundledPluginRetry(ILogger logger, Exception exception, string package, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Background loop '{Name}' stopped.")]
    private static partial void LogLoopStopped(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Background loop '{Name}' failed.")]
    private static partial void LogBackgroundFailed(ILogger logger, Exception exception, string name);
}

/// <summary>The ownership marker of this installation (<c>BlockerOwned=&lt;installId&gt;</c>), generated once and kept in a plain file.</summary>
public static class InstallIdStore
{
    /// <summary>Reads the install id, creating it when the file is missing; a damaged file is kept as <c>&lt;path&gt;.bad</c> before a new id replaces it.</summary>
    /// <param name="fileSystem">File system.</param>
    /// <param name="path">File path.</param>
    /// <returns>A 32-character hex id.</returns>
    public static string LoadOrCreate(IFileSystem fileSystem, string path)
    {
        if (fileSystem.File.Exists(path))
        {
            if (fileSystem.File.ReadAllText(path).Trim() is { Length: 32 } existing && existing.All(char.IsAsciiHexDigit))
            {
                return existing;
            }

            // The old id may still be referenced by BlockerOwned markers; keep it so an operator can sweep them by hand.
            fileSystem.File.Move(path, path + ".bad", overwrite: true);
        }

        string id = Guid.NewGuid().ToString("N");
        AtomicFile.Write(fileSystem, path, System.Text.Encoding.ASCII.GetBytes(id));
        return id;
    }
}
