using System.IO.Abstractions;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.AppRules;
using Blocker.Core.Clock;
using Blocker.Core.Ipc;
using Blocker.Core.Journal;
using Blocker.Core.Orchestration;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Security;
using Blocker.Core.Session;
using Blocker.Core.Supervision;
using Blocker.Core.WebFilter;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.ServiceHost;

/// <summary>
/// The composition of the running service: stores, rollback journal, verified time, session state machine, enforcement coordinator, the
/// service pipe with all IPC handlers, the plugin host and the loops that keep clients, plugins and the watchdog informed. Everything
/// platform-specific arrives through <see cref="BlockerServicePorts"/>. <see cref="DisposeAsync"/> stops all activity but never rolls an
/// active session back: the session and its journal survive for <see cref="StartAsync"/> after the next start.
/// </summary>
public sealed partial class BlockerServiceApp : IAsyncDisposable
{
    private static readonly TimeSpan TimeRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BundledRetryDelay = TimeSpan.FromSeconds(15);
    private const int BundledAttempts = 8;
    private static readonly TimeSpan TimeRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private readonly BlockerServicePorts _ports;
    private readonly BlockerPaths _paths;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<BlockerServiceApp> _logger;
    private readonly VerifiedTimeService _verified;
    private readonly SecureSessionStore _sessionStore;
    private readonly AppScanCache _scanCache;
    private readonly AppRuleEngine _appRules;
    private readonly AppRuleEnforcer _appEnforcer;
    private readonly SupervisionStatusFile _supervisionFile;
    private readonly IPluginHostGateway _gateway;
    private readonly IpcServer? _pluginServer;
    private readonly PluginHostSupervisor? _supervisor;
    private readonly PluginEventForwarder _forwarder;
    private readonly NotificationPump _notifications;
    private readonly List<IDisposable> _registrations = [];
    private readonly List<Task> _background = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ServiceIpcHandlers _handlers;
    private readonly ServiceSettingsStore _settings;
    private readonly BlockNoticeSwitch _blockNotice = new();
    private SessionStatusDto? _lastPublished;
    private bool? _lastActive;
    private Guid? _lastSessionId;
    private (SessionPhase Phase, Guid? Id, int Passes)? _lastHostState;
    private bool _watchdogEnsured;
    private int _disposed;

    private BlockerServiceApp(BlockerServicePorts ports, BlockerPaths paths, ILoggerFactory loggers, string installId, IReadOnlyList<InstalledApp> cachedScan, AppScanCache scanCache, SecureFileStore secure)
    {
        _ports = ports;
        _paths = paths;
        _loggers = loggers;
        _logger = loggers.CreateLogger<BlockerServiceApp>();
        _scanCache = scanCache;
        InstallId = installId;
        Journal = new RollbackJournal(secure, paths.JournalPath, ports.CreateChangeHandlers(installId), installId, loggers.CreateLogger<RollbackJournal>());
        _verified = new VerifiedTimeService(ports.TimeSources, ports.Clock, ports.Time);
        _sessionStore = new SecureSessionStore(secure, paths.SessionStorePath);
        _supervisionFile = new SupervisionStatusFile(secure, paths.SupervisionStatusPath, loggers.CreateLogger<SupervisionStatusFile>());
        var policyStore = new SecureSessionPolicyStore(secure, paths.SessionPolicyPath);
        _appRules = new AppRuleEngine(
            new AppRulesStore(secure, paths.AppRulesPath, loggers.CreateLogger<AppRulesStore>()),
            new AppUsageStore(secure, paths.AppUsagePath, loggers.CreateLogger<AppUsageStore>()),
            VerifiedOrSystemNow,
            static () => TimeZoneInfo.Local.Id,
            ports.Enforcement.WindowsDirectory);
        var pluginStore = new PluginStore(ports.FileSystem, paths.PluginsDirectory, paths.PluginDataDirectory);

        if (ports.PluginHostGateway is { } gateway)
        {
            _gateway = gateway;
        }
        else
        {
            PluginHostPorts host = ports.PluginHost!;
            PluginHostPipeName = IpcPipeNames.PluginHostPrefix + Guid.NewGuid().ToString("N");
            _pluginServer = new IpcServer(PluginHostPipeName, host.PipeFactory, host.IdentityResolver, static () => false, loggers.CreateLogger<IpcServer>(), time: ports.Time);
            _supervisor = new PluginHostSupervisor(
                _pluginServer,
                host.Launcher,
                ports.Time,
                loggers.CreateLogger<PluginHostSupervisor>(),
                new PluginHostSupervisorOptions
                {
                    HostExecutablePath = paths.PluginHostExecutablePath,
                    PipeName = PluginHostPipeName,
                    PluginsDirectory = paths.PluginsDirectory,
                    DataDirectory = paths.PluginDataDirectory,
                });
            _gateway = new SupervisorPluginHostGateway(_supervisor);
        }

        _forwarder = new PluginEventForwarder(_gateway, loggers.CreateLogger<PluginEventForwarder>());
        var agent = new AgentLink(pid => ports.ProcessMonitor.EnumerateRunning().FirstOrDefault(p => p.ProcessId == pid)?.StartTimeTicks, loggers.CreateLogger<AgentLink>());
        // The coordinator and the app rules both start and restart the one monitor; the wrapper keeps them from starting it twice.
        var monitor = new SharedProcessMonitor(ports.ProcessMonitor);
        var hasher = new FileHasher(ports.FileSystem);
        Machine = new SessionStateMachine(_sessionStore, _forwarder, ports.Clock, _verified, ports.BootMarker, ports.DevMode, ports.BreakGlassIterations);
        Coordinator = new EnforcementCoordinator(
            Machine,
            Journal,
            ports.ChangePlanner,
            policyStore,
            monitor,
            ports.ProcessController,
            ports.UserSessionLauncher,
            ports.SignatureVerifier,
            hasher,
            ports.BrowserFirewall,
            ports.ProxyFactory ?? ((policy, port) => new WebFilterProxy(policy, new WebFilterProxyOptions { Port = port }, logger: loggers.CreateLogger<WebFilterProxy>())),
            _forwarder,
            ports.Enforcement.ScannedApps.Count > 0 ? ports.Enforcement : ports.Enforcement with { ScannedApps = cachedScan },
            ports.Time,
            loggers.CreateLogger<EnforcementCoordinator>(),
            InterceptProcessAsync,
            (pid, ct) => agent.CloseProcessAsync(pid, ct),
            _blockNotice);

        _appEnforcer = new AppRuleEnforcer(
            _appRules,
            monitor,
            ports.ProcessController,
            ports.UserSessionLauncher,
            hasher,
            new CriticalProcesses(ports.Enforcement.WindowsDirectory, ports.Enforcement.InstallDirectory, ports.Enforcement.ExtraTrustedRoots),
            ports.Enforcement.StubExecutablePath,
            _forwarder,
            () => Coordinator.IsSessionActive,
            () => agent.IsConnected,
            ports.Time,
            loggers.CreateLogger<AppRuleEnforcer>(),
            _blockNotice);

        Server = new IpcServer(
            ports.ServicePipeName,
            ports.ServicePipeFactory,
            ports.ServiceIdentityResolver,
            () => Coordinator.IsSessionActive,
            loggers.CreateLogger<IpcServer>(),
            ports.IpcOptions,
            ports.Time);
        var handlers = new ServiceIpcHandlers(
            new ServiceHandlerDependencies(
                Coordinator,
                policyStore,
                new DraftAllowlistStore(secure, paths.DraftAllowlistPath),
                _settings = new ServiceSettingsStore(secure, paths.SettingsPath),
                scanCache,
                ports.AppScanner,
                pluginStore,
                _gateway,
                ports.ProcessController,
                _forwarder,
                _verified,
                ports.Time,
                agent,
                ports.DevMode,
                ports.PluginTemplate,
                SyncStatusAsync,
                new AppSetsStore(secure, paths.AppSetsPath, loggers.CreateLogger<AppSetsStore>()),
                _appRules,
                _appEnforcer,
                _blockNotice),
            loggers.CreateLogger<ServiceIpcHandlers>());
        _registrations.AddRange(handlers.RegisterAll(Server));
        _handlers = handlers;
        if (_pluginServer is not null && _supervisor is { } supervisor)
        {
            // The capabilities that reach beyond the plugin host's sandbox (starting sessions, toasts).
            var hostRequests = new PluginHostRequestHandlers(
                new PluginHostRequestDependencies(
                    handlers.ResolvePluginAsync,
                    agent,
                    handlers.StartPluginSessionAsync,
                    ports.Time),
                loggers.CreateLogger<PluginHostRequestHandlers>());
            _registrations.AddRange(hostRequests.RegisterAll(_pluginServer, supervisor.IsCurrentHost));
        }

        _notifications = new NotificationPump(agent, Server, loggers.CreateLogger<NotificationPump>());
        Coordinator.Notification += OnNotification;
    }

    /// <summary>Gets the ownership marker of this installation.</summary>
    public string InstallId { get; }

    /// <summary>Gets the name of the plugin-host pipe; <see langword="null"/> when a ready gateway was supplied.</summary>
    public string? PluginHostPipeName { get; }

    /// <summary>Gets the latest session snapshot.</summary>
    public SessionSnapshot Snapshot => Coordinator.Snapshot;

    /// <summary>Gets the coordinator (tests drive its tick directly).</summary>
    internal EnforcementCoordinator Coordinator { get; }

    /// <summary>Gets the per-app rules (tests read the metered usage).</summary>
    internal AppRuleEngine AppRules => _appRules;

    /// <summary>Gets the enforcer of the per-app rules (tests drive its tick directly).</summary>
    internal AppRuleEnforcer AppEnforcer => _appEnforcer;

    /// <summary>Gets the session state machine.</summary>
    internal SessionStateMachine Machine { get; }

    /// <summary>Gets the rollback journal.</summary>
    internal RollbackJournal Journal { get; }

    /// <summary>Gets the service pipe server.</summary>
    internal IpcServer Server { get; }

    /// <summary>Gets the verified clock service.</summary>
    internal VerifiedTimeService VerifiedTime => _verified;

    /// <summary>
    /// Builds and starts the service: loads the stores, establishes verified time, restores a persisted session (rolling its protections back
    /// and re-applying them, or rolling leftovers back when none runs), starts the service pipe, the plugin host and the background loops.
    /// </summary>
    /// <param name="ports">Platform dependencies.</param>
    /// <param name="paths">File locations.</param>
    /// <param name="loggers">Logger factory.</param>
    /// <param name="cancellationToken">Aborts the start.</param>
    /// <returns>The running service.</returns>
    public static async Task<BlockerServiceApp> StartAsync(BlockerServicePorts ports, BlockerPaths paths, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(loggers);
        ports.Validate();
        foreach (string directory in new[] { paths.DataRoot, paths.PluginsDirectory, paths.PluginDataDirectory })
        {
            _ = ports.FileSystem.Directory.CreateDirectory(directory);
        }

        ProtectDataRoot(ports, paths, loggers.CreateLogger<BlockerServiceApp>());

        string installId = InstallIdStore.LoadOrCreate(ports.FileSystem, paths.InstallIdPath);
        var secure = new SecureFileStore(ports.FileSystem, ports.SecretProtector, new HmacKeyStore(ports.FileSystem, ports.SecretProtector, paths.HmacKeyPath));
        var scanCache = new AppScanCache(secure, paths.AppScanCachePath);
        IReadOnlyList<InstalledApp> cachedScan = await scanCache.LoadAsync(cancellationToken).ConfigureAwait(false);

        var app = new BlockerServiceApp(ports, paths, loggers, installId, cachedScan, scanCache, secure);
        try
        {
            await app.InitializeAsync(cachedScan.Count == 0 && ports.Enforcement.ScannedApps.Count == 0, cancellationToken).ConfigureAwait(false);
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ProtectDataRoot(BlockerServicePorts ports, BlockerPaths paths, ILogger logger)
    {
        try
        {
            ports.ProtectDataRoot?.Invoke(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(ex, "Protecting the data root {Path} failed; sealed state stays readable and writable to whoever the parent directory allows.", paths.DataRoot);
        }
    }

    /// <summary>Stops every loop, the pipes, the plugin host and the coordinator; an active session stays intact.</summary>
    /// <returns>A task that completes when everything stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        Coordinator.Notification -= OnNotification;
        await Server.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_background).ConfigureAwait(false);
        await _appEnforcer.DisposeAsync().ConfigureAwait(false);
        await _notifications.DisposeAsync().ConfigureAwait(false);
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }

        if (_pluginServer is not null)
        {
            await _pluginServer.DisposeAsync().ConfigureAwait(false);
        }

        await Coordinator.DisposeAsync().ConfigureAwait(false);
        await _forwarder.DisposeAsync().ConfigureAwait(false);
        await _ports.ProcessMonitor.DisposeAsync().ConfigureAwait(false);
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _stop.Dispose();
        LogStopped(_logger);
    }

    private async Task InitializeAsync(bool scanIfNoCache, CancellationToken cancellationToken)
    {
        _ = await _verified.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await LoadAppRulesAsync(cancellationToken).ConfigureAwait(false);
        _blockNotice.Enabled = ServiceSettingKeys.ShowsBlockNotice(await _settings.GetAsync(cancellationToken).ConfigureAwait(false));
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        Server.Start();
        if (_pluginServer is not null && _supervisor is not null)
        {
            _pluginServer.Start();
            _supervisor.Start();
        }

        _background.Add(RunGuardedAsync("Bundled plugins", InstallBundledPluginsAsync));

        _background.Add(RunGuardedAsync("Time refresh", RefreshTimeLoopAsync));
        _background.Add(RunGuardedAsync("Status", StatusLoopAsync));
        await StartAppEnforcerAsync(cancellationToken).ConfigureAwait(false);
        _background.Add(RunGuardedAsync("App rules", _appEnforcer.RunAsync));
        if (_ports.ServiceControl is { } control && _ports.ProcessWaiter is { } waiter)
        {
            var supervisor = new MutualSupervisor(
                BlockerServiceNames.Watchdog,
                control,
                waiter,
                _ => ValueTask.FromResult(Coordinator.IsSessionActive),
                _ports.Time,
                _loggers.CreateLogger<MutualSupervisor>());
            _background.Add(RunGuardedAsync("Watchdog supervision", supervisor.RunAsync));
        }

        if (scanIfNoCache && !Coordinator.IsSessionActive)
        {
            _background.Add(RunGuardedAsync("Initial application scan", ScanOnceAsync));
        }

        await SyncStatusAsync(cancellationToken).ConfigureAwait(false);
        LogStarted(_logger, Snapshot.Phase);
    }

    // The per-app rules are an add-on to the sessions: a file or a monitor that cannot be used must not keep a running session from being
    // recovered or the service from starting, so these two steps degrade to "no rules" instead of failing the start.
    private async ValueTask LoadAppRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _appRules.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogAppRulesLoadFailed(_logger, ex);
        }
    }

    private async ValueTask StartAppEnforcerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _appEnforcer.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogAppEnforcerStartFailed(_logger, ex);
        }
    }
}
