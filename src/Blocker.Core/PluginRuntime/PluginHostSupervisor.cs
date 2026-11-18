using System.Security.Cryptography;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Ports;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>State of the <see cref="PluginHostSupervisor"/>.</summary>
public enum PluginHostState
{
    /// <summary>Not started, or stopped.</summary>
    Stopped,

    /// <summary>The first host is launching.</summary>
    Starting,

    /// <summary>The host is registered and plugins are loaded.</summary>
    Running,

    /// <summary>The host ended unexpectedly and is being relaunched.</summary>
    Restarting,

    /// <summary>The host kept crashing; plugins are unavailable until the service restarts. Every call falls back safely.</summary>
    Unavailable,
}

/// <summary>Paths, timeouts and restart policy of a <see cref="PluginHostSupervisor"/>.</summary>
public sealed record PluginHostSupervisorOptions
{
    /// <summary>Gets the path of <c>Blocker.PluginHost.exe</c>.</summary>
    public required string HostExecutablePath { get; init; }

    /// <summary>Gets the name of the plugin-host pipe served by the <see cref="IpcServer"/> given to the supervisor.</summary>
    public required string PipeName { get; init; }

    /// <summary>Gets the directory of installed plugins.</summary>
    public required string PluginsDirectory { get; init; }

    /// <summary>Gets the directory of plugin data.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Gets how long a launched host may take to register.</summary>
    public TimeSpan RegisterTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the total time one interceptor round trip may take; the verdict is abstain when exceeded.</summary>
    public TimeSpan InterceptTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets the time for a lock-policy round trip.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets the time for rendering a slot and for forwarding an action.</summary>
    public TimeSpan UiTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets the time for a plugin IPC call.</summary>
    public TimeSpan InvokeTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the time for session and event notifications.</summary>
    public TimeSpan NotifyTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the time for loading plugins (every plugin is compiled with Roslyn).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the time for evaluating a package (compile and run its tests).</summary>
    public TimeSpan EvaluateTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets how long a stopping supervisor waits for the host to exit before killing it.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets the number of restarts allowed within <see cref="RestartWindow"/>; the next crash makes the host unavailable.</summary>
    public int MaxRestarts { get; init; } = 5;

    /// <summary>Gets the window of <see cref="MaxRestarts"/>.</summary>
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the first delay before a relaunch; it doubles per crash within the window.</summary>
    public TimeSpan RestartDelayMin { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the largest delay before a relaunch.</summary>
    public TimeSpan RestartDelayMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the number of consecutive timed-out calls after which the host is considered hung and killed.</summary>
    public int ConsecutiveTimeoutsToKill { get; init; } = 5;
}

/// <summary>
/// Service-side owner of the plugin host process. Launches it restricted, accepts its registration on the plugin-host pipe, restarts it
/// with back-off when it dies (at most <see cref="PluginHostSupervisorOptions.MaxRestarts"/> within the window, then plugins are
/// unavailable), kills it when it stops answering, and exposes typed calls for the <c>host.*</c> methods. Callers never need to handle
/// host failures: every call that feeds enforcement falls back to the safe answer (abstain, allow, nothing). Package evaluation runs in
/// a separate short-lived host that is killed afterwards.
/// </summary>
public sealed partial class PluginHostSupervisor : IAsyncDisposable
{
    private readonly IpcServer _server;
    private readonly IRestrictedProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly ILogger<PluginHostSupervisor> _logger;
    private readonly PluginHostSupervisorOptions _options;
    private readonly Dictionary<string, TaskCompletionSource<IpcPeer>> _pending = [];
    private readonly Queue<long> _crashes = new();
    private readonly List<(PluginHostState State, TaskCompletionSource Signal)> _waiters = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _evaluateGate = new(1, 1);
    private readonly List<IDisposable> _registrations = [];
    private IReadOnlyList<HostPluginStatusDto> _plugins = [];
    private SessionChangedRequest _lastSession;
    private RunningHost? _current;
    private PluginHostState _state = PluginHostState.Stopped;
    private Task _monitor = Task.CompletedTask;
    private int _consecutiveTimeouts;
    private int _started;
    private int _disposed;

    /// <summary>Creates the supervisor and registers its handlers on <paramref name="server"/>; call <see cref="Start"/> to launch the host.</summary>
    /// <param name="server">Server of the plugin-host pipe (role <see cref="IpcCallerRole.PluginHost"/>); one supervisor per server.</param>
    /// <param name="launcher">Starts the restricted host process.</param>
    /// <param name="time">Time source for timeouts and back-off.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Paths and policy.</param>
    public PluginHostSupervisor(
        IpcServer server,
        IRestrictedProcessLauncher launcher,
        TimeProvider time,
        ILogger<PluginHostSupervisor> logger,
        PluginHostSupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _server = server;
        _launcher = launcher;
        _time = time;
        _logger = logger;
        _options = options;
        _lastSession = new SessionChangedRequest(SessionStatusDto.From(SessionSnapshot.Idle), time.GetUtcNow());
        _registrations.Add(server.Register<PluginHostRegisterRequest, PluginHostRegisterResponse>(IpcMethods.PluginHostRegister, OnRegister));
        _registrations.Add(server.Register<PluginFaultedNotification, EmptyDto>(IpcMethods.PluginHostPluginFaulted, OnFaulted));
        _registrations.Add(server.Register<RenderRequestedNotification, EmptyDto>(IpcMethods.PluginHostRenderRequested, OnRenderRequested));
    }

    /// <summary>Raised when the state changes.</summary>
    public event EventHandler<PluginHostState>? StateChanged;

    /// <summary>Raised when the host reports that a plugin's circuit breaker tripped.</summary>
    public event EventHandler<PluginFaultedNotification>? PluginFaulted;

    /// <summary>Raised when a plugin contribution asks to be rendered again.</summary>
    public event EventHandler<RenderRequestedNotification>? RenderRequested;

    /// <summary>Gets the current state.</summary>
    public PluginHostState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the plugins as last reported by the host (empty while the host is down or unavailable).</summary>
    public IReadOnlyList<HostPluginStatusDto> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _plugins;
            }
        }
    }

    /// <summary>Launches the host and keeps it running. Calling it again has no effect.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _monitor = Task.Run(() => MonitorAsync(_stop.Token));
        }
    }

    /// <summary>Completes when the supervisor is in <paramref name="state"/> (immediately if it already is).</summary>
    /// <param name="state">The awaited state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the state is reached.</returns>
    public Task WaitForStateAsync(PluginHostState state, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((state, signal));
            return signal.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Tells the host to (re)load the enabled plugins, for example after an install, removal or enable change.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The plugins as reported by the host; the previous list when the host does not answer.</returns>
    public async ValueTask<IReadOnlyList<HostPluginStatusDto>> LoadPluginsAsync(CancellationToken cancellationToken)
    {
        SessionChangedRequest session = Volatile.Read(ref _lastSession);
        LoadPluginsResponse response = await TryCallAsync(
            IpcMethods.HostLoadPlugins,
            new LoadPluginsRequest(session.Status, session.VerifiedUtcNow),
            _options.LoadTimeout,
            new LoadPluginsResponse(Plugins),
            cancellationToken).ConfigureAwait(false);
        SetPlugins(response.Plugins);
        return response.Plugins;
    }

    /// <summary>Asks the process interceptors of all plugins about a new process. Anything but an answer in time is <see cref="InterceptResponse.Abstain"/>.</summary>
    /// <param name="process">The new process.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict.</returns>
    public ValueTask<InterceptResponse> InterceptProcessAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
        TryCallAsync(IpcMethods.HostInterceptProcess, new InterceptProcessRequest(process), _options.InterceptTimeout, InterceptResponse.Abstain, cancellationToken);

    /// <summary>Asks the window interceptors of all plugins about a new foreground window. Anything but an answer in time is abstain.</summary>
    /// <param name="window">The window.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict.</returns>
    public ValueTask<InterceptResponse> InterceptWindowAsync(WindowDescriptor window, CancellationToken cancellationToken) =>
        TryCallAsync(IpcMethods.HostInterceptWindow, new InterceptWindowRequest(window), _options.InterceptTimeout, InterceptResponse.Abstain, cancellationToken);

    /// <summary>Collects lock-policy vetoes. Anything but an answer in time means nobody objects.</summary>
    /// <param name="action">The voluntary action.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The vetoes.</returns>
    public ValueTask<EvaluateLockResponse> EvaluateLockAsync(LockAction action, CancellationToken cancellationToken) =>
        TryCallAsync(IpcMethods.HostEvaluateLock, new EvaluateLockRequest(action), _options.LockTimeout, new EvaluateLockResponse([]), cancellationToken);

    /// <summary>Renders all contributions of a slot. Anything but an answer in time is an empty slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The serialized trees; the receiver must still deserialize and validate them.</returns>
    public ValueTask<GetContributionsResponse> RenderSlotAsync(UiSlot slot, CancellationToken cancellationToken) =>
        TryCallAsync(IpcMethods.HostRenderSlot, new GetContributionsRequest(slot), _options.UiTimeout, new GetContributionsResponse(slot, []), cancellationToken);

    /// <summary>Forwards a user interaction to a plugin contribution.</summary>
    /// <param name="request">The interaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome; a failure response when the host does not answer.</returns>
    public ValueTask<OperationResponse> SendUiActionAsync(SendActionRequest request, CancellationToken cancellationToken) =>
        TryCallAsync(IpcMethods.HostSendUiAction, request, _options.UiTimeout, new OperationResponse(false, ["The plugin host is not available."]), cancellationToken);

    /// <summary>Forwards a plugin IPC call (<c>plugin.{id}.{method}</c>).</summary>
    /// <param name="pluginId">Owning plugin.</param>
    /// <param name="method">Plugin-local method name.</param>
    /// <param name="payloadJson">Request JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response JSON.</returns>
    /// <exception cref="IpcException">The host or the plugin refused the call, or the host did not answer.</exception>
    public async ValueTask<string> InvokePluginMethodAsync(string pluginId, string method, string payloadJson, CancellationToken cancellationToken)
    {
        try
        {
            InvokePluginMethodResponse response = await CallAsync<InvokePluginMethodRequest, InvokePluginMethodResponse>(
                IpcMethods.HostInvokePluginMethod, new InvokePluginMethodRequest(pluginId, method, payloadJson), _options.InvokeTimeout, cancellationToken).ConfigureAwait(false);
            return response.PayloadJson;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or PluginHostUnavailableException)
        {
            throw new IpcException(IpcErrorCode.Internal, "The plugin host is not available.");
        }
    }

    /// <summary>Tells the host about a session change; the latest state is also replayed to a restarted host.</summary>
    /// <param name="snapshot">The new session state.</param>
    /// <param name="verifiedUtcNow">Network-verified UTC time right now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the host acknowledged or the call failed (failures are logged).</returns>
    public async ValueTask NotifySessionChangedAsync(SessionSnapshot snapshot, DateTimeOffset verifiedUtcNow, CancellationToken cancellationToken)
    {
        var request = new SessionChangedRequest(SessionStatusDto.From(snapshot), verifiedUtcNow);
        Volatile.Write(ref _lastSession, request);
        _ = await TryCallAsync(IpcMethods.HostSessionChanged, request, _options.NotifyTimeout, new EmptyDto(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivers a core event to the plugins. Custom plugin events are ignored; failures are logged.</summary>
    /// <param name="evt">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the host acknowledged or the call failed.</returns>
    public async ValueTask PublishEventAsync(IBlockerEvent evt, CancellationToken cancellationToken)
    {
        if (HostEventDto.From(evt) is { } dto)
        {
            _ = await TryCallAsync(IpcMethods.HostPublishEvent, dto, _options.NotifyTimeout, new EmptyDto(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the whole install pipeline, tests included, in a separate short-lived host that is killed afterwards, so a hanging or
    /// crashing plugin cannot touch the running host.
    /// </summary>
    /// <param name="package">The plugin ZIP.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report; <see cref="PluginInstallStageDto.Host"/> when the evaluation host failed or timed out.</returns>
    public async ValueTask<PluginInstallReportDto> EvaluatePackageAsync(ReadOnlyMemory<byte> package, CancellationToken cancellationToken)
    {
        string base64 = Convert.ToBase64String(package.Span);
        if (base64.Length > IpcLimits.MaxMessageBytes - 1024)
        {
            return HostFailure("The package is too large to be evaluated.");
        }

        await _evaluateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunningHost? host = null;
            try
            {
                host = await LaunchAsync(cancellationToken).ConfigureAwait(false);
                JsonElement result = await host.Peer.InvokeAsync(
                    IpcMethods.HostEvaluatePackage, IpcJson.ToElement(new EvaluatePackageRequest(base64)), _options.EvaluateTimeout, cancellationToken).ConfigureAwait(false);
                return IpcJson.FromElement<PluginInstallReportDto>(result);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or IpcException or JsonException)
            {
                LogEvaluationFailed(_logger, ex.GetType().Name);
                return HostFailure("The plugin check did not complete. Try again.");
            }
            finally
            {
                if (host is not null)
                {
                    StopProcess(host.Process);
                }
            }
        }
        finally
        {
            _evaluateGate.Release();
        }
    }

    /// <summary>Asks the host to shut down, kills it if it does not exit in time, and stops supervising.</summary>
    /// <returns>A task that completes when the host is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (Volatile.Read(ref _current) is { } host)
        {
            await ShutDownGracefullyAsync(host).ConfigureAwait(false);
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        SetState(PluginHostState.Stopped);
        _stop.Dispose();
        _evaluateGate.Dispose();
    }

    private static PluginInstallReportDto HostFailure(string message) =>
        new(PluginInstallStageDto.Host, [message], null, null, null, null, [], 0, 0);

    private ValueTask<PluginHostRegisterResponse> OnRegister(IpcRequestContext context, PluginHostRegisterRequest request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IpcPeer>? waiting = null;
        lock (_gate)
        {
            if (request.ProtocolVersion == PluginHostProtocol.Version && request.Token is { } token && _pending.Remove(token, out TaskCompletionSource<IpcPeer>? found))
            {
                waiting = found;
            }
        }

        bool accepted = waiting?.TrySetResult(context.Peer) == true;
        if (!accepted)
        {
            LogRegistrationRefused(_logger);
        }

        return ValueTask.FromResult(new PluginHostRegisterResponse(accepted, PluginHostProtocol.Version));
    }

    private ValueTask<EmptyDto> OnFaulted(IpcRequestContext context, PluginFaultedNotification notification, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _current)?.Peer == context.Peer)
        {
            lock (_gate)
            {
                _plugins = [.. _plugins.Select(p => p.PluginId == notification.PluginId ? p with { State = HostPluginState.Faulted, Error = notification.Reason } : p)];
            }

            LogPluginFaulted(_logger, notification.PluginId);
            Raise(PluginFaulted, notification);
        }

        return ValueTask.FromResult(new EmptyDto());
    }

    private ValueTask<EmptyDto> OnRenderRequested(IpcRequestContext context, RenderRequestedNotification notification, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _current)?.Peer == context.Peer)
        {
            Raise(RenderRequested, notification);
        }

        return ValueTask.FromResult(new EmptyDto());
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        try
        {
            handler?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            LogSubscriberFailed(_logger, ex);
        }
    }

    private async Task MonitorAsync(CancellationToken stop)
    {
        bool first = true;
        while (!stop.IsCancellationRequested)
        {
            SetState(first ? PluginHostState.Starting : PluginHostState.Restarting);
            first = false;
            RunningHost? host = await TryLaunchAsync(stop).ConfigureAwait(false);
            if (host is not null)
            {
                Volatile.Write(ref _current, host);
                Interlocked.Exchange(ref _consecutiveTimeouts, 0);
                await LoadAfterLaunchAsync(stop).ConfigureAwait(false);
                SetState(PluginHostState.Running);
                await Task.WhenAny(host.Process.WaitForExitAsync(stop), host.Peer.Disconnected).ConfigureAwait(false);
                Volatile.Write(ref _current, null);
                SetPlugins([]);
                StopProcess(host.Process);
            }

            if (stop.IsCancellationRequested)
            {
                break;
            }

            int crashes = RecordCrash();
            if (crashes > _options.MaxRestarts)
            {
                LogUnavailable(_logger, crashes, _options.RestartWindow);
                SetState(PluginHostState.Unavailable);
                return;
            }

            TimeSpan delay = TimeSpan.FromTicks(Math.Min(_options.RestartDelayMin.Ticks << (crashes - 1), _options.RestartDelayMax.Ticks));
            LogRestarting(_logger, crashes, delay);
            SetState(PluginHostState.Restarting);
            try
            {
                await Task.Delay(delay, _time, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
        }

        SetState(PluginHostState.Stopped);
    }

    private async ValueTask<RunningHost?> TryLaunchAsync(CancellationToken stop)
    {
        try
        {
            return await LaunchAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Launching touches the OS (token, job object, process creation) and can fail in many ways; all of them mean "no host this time".
            LogLaunchFailed(_logger, ex);
            return null;
        }
    }

    private async Task<RunningHost> LaunchAsync(CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var registered = new TaskCompletionSource<IpcPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[token] = registered;
        }

        IRestrictedProcess? process = null;
        try
        {
            var arguments = new PluginHostArguments(_options.PipeName, _options.PluginsDirectory, _options.DataDirectory, token);
            process = _launcher.Launch(_options.HostExecutablePath, arguments.Format());
            Task<IpcPeer> peerTask = registered.Task.WaitAsync(_options.RegisterTimeout, _time, cancellationToken);
            Task exited = process.WaitForExitAsync(cancellationToken);
            if (await Task.WhenAny(peerTask, exited).ConfigureAwait(false) == exited && !peerTask.IsCompleted)
            {
                throw new IOException($"The plugin host exited before it registered (exit code {process.ExitCode?.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}).");
            }

            return new RunningHost(process, await peerTask.ConfigureAwait(false));
        }
        catch
        {
            if (process is not null)
            {
                StopProcess(process);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(token);
            }
        }
    }

    private async Task LoadAfterLaunchAsync(CancellationToken stop)
    {
        try
        {
            _ = await LoadPluginsAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    private int RecordCrash()
    {
        long now = _time.GetTimestamp();
        _crashes.Enqueue(now);
        while (_crashes.Count > 0 && _time.GetElapsedTime(_crashes.Peek(), now) >= _options.RestartWindow)
        {
            _crashes.Dequeue();
        }

        return _crashes.Count;
    }

    private void StopProcess(IRestrictedProcess process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            LogKillFailed(_logger, ex.GetType().Name);
        }

        process.Dispose();
    }

    private async Task ShutDownGracefullyAsync(RunningHost host)
    {
        try
        {
            _ = await host.Peer.InvokeAsync(IpcMethods.HostShutdown, IpcJson.ToElement(new EmptyDto()), _options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            await host.Process.WaitForExitAsync(CancellationToken.None).WaitAsync(_options.ShutdownTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or IpcException)
        {
            LogShutdownForced(_logger, ex.GetType().Name);
        }
    }

    private async ValueTask<TResponse> TryCallAsync<TRequest, TResponse>(string method, TRequest request, TimeSpan timeout, TResponse fallback, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync<TRequest, TResponse>(method, request, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or IpcException or PluginHostUnavailableException)
        {
            LogCallFailed(_logger, method, ex.GetType().Name);
            return fallback;
        }
    }

    private async ValueTask<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        RunningHost host = Volatile.Read(ref _current) ?? throw new PluginHostUnavailableException();
        JsonElement result;
        try
        {
            result = await host.Peer.InvokeAsync(method, IpcJson.ToElement(request), timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (Interlocked.Increment(ref _consecutiveTimeouts) >= _options.ConsecutiveTimeoutsToKill)
            {
                LogHung(_logger, _options.ConsecutiveTimeoutsToKill);
                Interlocked.Exchange(ref _consecutiveTimeouts, 0);
                StopProcess(host.Process);
            }

            throw;
        }

        Interlocked.Exchange(ref _consecutiveTimeouts, 0);
        try
        {
            return IpcJson.FromElement<TResponse>(result);
        }
        catch (JsonException)
        {
            throw new IpcException(IpcErrorCode.InvalidRequest, "Unexpected response.");
        }
    }

    private void SetPlugins(IReadOnlyList<HostPluginStatusDto> plugins)
    {
        lock (_gate)
        {
            _plugins = plugins;
        }
    }

    private void SetState(PluginHostState state)
    {
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].State == state)
                {
                    ready.Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }
        }

        LogState(_logger, state);
        foreach (TaskCompletionSource signal in ready)
        {
            signal.TrySetResult();
        }

        Raise(StateChanged, state);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin host state: {State}.")]
    private static partial void LogState(ILogger logger, PluginHostState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin host registration refused.")]
    private static partial void LogRegistrationRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} reported faulted by the host.")]
    private static partial void LogPluginFaulted(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscriber of a plugin host event failed.")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The plugin host could not be launched.")]
    private static partial void LogLaunchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin host ended ({Crashes} within the window); relaunching in {Delay}.")]
    private static partial void LogRestarting(ILogger logger, int crashes, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin host ended {Crashes} times within {Window}; plugins are unavailable.")]
    private static partial void LogUnavailable(ILogger logger, int crashes, TimeSpan window);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin host call {Method} failed ({Reason}).")]
    private static partial void LogCallFailed(ILogger logger, string method, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin host did not answer {Count} calls in a row; killing it.")]
    private static partial void LogHung(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin evaluation host failed ({Reason}).")]
    private static partial void LogEvaluationFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Killing the plugin host process failed ({Reason}).")]
    private static partial void LogKillFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin host did not shut down gracefully ({Reason}); it is killed.")]
    private static partial void LogShutdownForced(ILogger logger, string reason);

    private sealed record RunningHost(IRestrictedProcess Process, IpcPeer Peer);

    private sealed class PluginHostUnavailableException : InvalidOperationException
    {
        public PluginHostUnavailableException()
            : base("The plugin host is not running.")
        {
        }
    }
}
