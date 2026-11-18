using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Sdk.Session;
using Blocker.UI.Native;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.UI.Agent;

/// <summary>
/// The user-session agent (<c>--agent</c>): registers with the service, reports foreground-window changes and idle state, and answers
/// the service's requests to close a process gracefully or show a toast. It never decides anything.
/// </summary>
internal sealed partial class AgentService : IHostedService, IConnectionInitializer, IDisposable
{
    /// <summary>Protocol version announced in <c>agent.register</c>.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>No input for this long counts as idle.</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EmergencyWarning = TimeSpan.FromSeconds(65);

    private readonly ServiceConnection _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly IToastPresenter _toasts;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentService> _logger;
    private readonly List<IDisposable> _registrations = [];
    private DispatcherTimer? _idleTimer;
    private nint _hook;
    private bool _isIdle;
    private bool _idleReported;

    /// <summary>Creates the agent.</summary>
    /// <param name="connection">Connection to the service.</param>
    /// <param name="dispatcher">UI-thread marshalling.</param>
    /// <param name="toasts">Toast presenter.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    public AgentService(ServiceConnection connection, IUiDispatcher dispatcher, IToastPresenter toasts, TimeProvider time, ILogger<AgentService> logger)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _toasts = toasts;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registrations.Add(_connection.Client.Register(IpcMethods.AgentCloseProcess, HandleCloseProcessAsync));
        _registrations.Add(_connection.Client.Register(IpcMethods.AgentNotify, HandleNotify));

        // The hook and the idle timer must live on the UI thread, which pumps the messages the hook callback needs.
        _dispatcher.Post(() =>
        {
            _hook = Win32Interop.HookForegroundChanges(OnForegroundChanged);
            _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = IdlePollInterval };
            _idleTimer.Tick += (_, _) => PollIdle();
            _idleTimer.Start();
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _dispatcher.Post(() =>
        {
            _idleTimer?.Stop();
            Win32Interop.UnhookForegroundChanges(_hook);
            _hook = 0;
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnConnectedAsync(IpcClient client, CancellationToken cancellationToken)
    {
        AgentRegisterResponse response = await client.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(
            IpcMethods.AgentRegister,
            new AgentRegisterRequest(ProtocolVersion, Process.GetCurrentProcess().SessionId),
            cancellationToken).ConfigureAwait(false);
        if (!response.Accepted)
        {
            throw new IOException("The service did not accept the agent.");
        }

        // A fresh connection knows nothing: resend the current state.
        _dispatcher.Post(() =>
        {
            _idleReported = false;
            PollIdle();
            OnForegroundChanged(Win32Interop.GetForegroundWindowHandle());
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
    }

    private async ValueTask<JsonElement> HandleCloseProcessAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        CloseProcessRequest request = ParseOrThrow<CloseProcessRequest>(parameters);
        bool closed = await ProcessCloser.CloseAsync(
            request.ProcessId,
            request.StartTimeTicks,
            TimeSpan.FromMilliseconds(request.TimeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
        return IpcJson.ToElement(new CloseProcessResponse(closed));
    }

    private ValueTask<JsonElement> HandleNotify(JsonElement parameters, CancellationToken cancellationToken)
    {
        NotificationDto notification = ParseOrThrow<NotificationDto>(parameters);
        _dispatcher.Post(() =>
        {
            SessionSnapshot session = _connection.Session;
            // The emergency-pass warning carries no number on the wire; the live countdown comes from the pushed session state.
            TimeSpan? countdown = notification.Level == NotificationLevel.Warning
                && session.Phase == SessionPhase.EmergencyPass
                && session.EmergencyPassRemaining is { } left
                && left <= EmergencyWarning
                    ? left
                    : null;
            _toasts.Show(new ToastViewModel(notification, countdown, _time));
        });
        return ValueTask.FromResult(IpcJson.ToElement(new EmptyDto()));
    }

    private static T ParseOrThrow<T>(JsonElement element)
    {
        try
        {
            return IpcJson.FromElement<T>(element);
        }
        catch (JsonException)
        {
            throw new IpcException(IpcErrorCode.InvalidRequest, "Malformed request.");
        }
    }

    // Runs on the UI thread from the WinEvent hook. It must not throw (see Win32Interop), so every failure is logged and dropped.
    private void OnForegroundChanged(nint window)
    {
        try
        {
            if (!_connection.Client.IsConnected)
            {
                return;
            }

            uint processId = window != 0 ? Win32Interop.GetWindowProcessId(window) : 0;
            if (processId == 0 || !Win32Interop.TryGetProcessInfo(processId, out long startTicks, out string? imagePath))
            {
                return;
            }

            ForegroundWindowDto dto = new((int)processId, startTicks, imagePath);
            _ = SendAsync(IpcMethods.AgentForegroundWindow, IpcJson.ToElement(dto));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            LogForegroundFailed(_logger, ex);
        }
    }

    private void PollIdle()
    {
        if (Win32Interop.GetIdleMilliseconds() is not { } idleMs)
        {
            return;
        }

        bool idle = idleMs >= IdleThreshold.TotalMilliseconds;
        if (_idleReported && idle == _isIdle)
        {
            return;
        }

        if (!_connection.Client.IsConnected)
        {
            return;
        }

        _isIdle = idle;
        _idleReported = true;
        _ = SendAsync(IpcMethods.AgentIdleChanged, IpcJson.ToElement(new IdleChangedDto(idle, (int)(idleMs / 1000))));
    }

    private async Task SendAsync(string method, JsonElement payload)
    {
        try
        {
            await _connection.Client.NotifyAsync(method, payload, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or IpcException or ObjectDisposedException)
        {
            LogSendFailed(_logger, ex, method);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not report the foreground window.")]
    private static partial void LogForegroundFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not send {Method} to the service.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, string method);
}
