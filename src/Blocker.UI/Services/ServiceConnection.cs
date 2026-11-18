using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Sdk.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.UI.Services;

/// <summary>Runs extra setup after every (re)connect to the service, for example agent registration.</summary>
internal interface IConnectionInitializer
{
    /// <summary>Called after the connection is up and the event subscription is made. A failure drops the connection and retries.</summary>
    /// <param name="client">The connected client.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when setup is done.</returns>
    Task OnConnectedAsync(IpcClient client, CancellationToken cancellationToken);
}

/// <summary>
/// Hosted wrapper around <see cref="IpcClient"/>: keeps the pipe connected (with back-off), subscribes to server pushes
/// after every connect and exposes the latest session state to the view models.
/// </summary>
internal sealed partial class ServiceConnection : BackgroundService, IServiceConnection
{
    private static readonly TimeSpan LivenessPoll = TimeSpan.FromMilliseconds(500);

    private readonly IUiDispatcher _dispatcher;
    private readonly IEnumerable<IConnectionInitializer> _initializers;
    private readonly ILogger<ServiceConnection> _logger;
    private readonly TimeProvider _time;
    private volatile SessionSnapshot _session = SessionSnapshot.Idle;
    private volatile bool _isConnected;

    /// <summary>Creates the connection; it connects when the host starts.</summary>
    /// <param name="client">Client of the service pipe.</param>
    /// <param name="dispatcher">UI-thread marshalling.</param>
    /// <param name="initializers">Extra per-connect setup.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Time source.</param>
    public ServiceConnection(
        IpcClient client,
        IUiDispatcher dispatcher,
        IEnumerable<IConnectionInitializer> initializers,
        ILogger<ServiceConnection> logger,
        TimeProvider time)
    {
        Client = client;
        _dispatcher = dispatcher;
        _initializers = initializers;
        _logger = logger;
        _time = time;
        Client.NotificationReceived += OnNotificationReceived;
    }

    /// <inheritdoc />
    public event EventHandler? ConnectionChanged;

    /// <inheritdoc />
    public event EventHandler? SessionChanged;

    /// <inheritdoc />
    public event EventHandler<NotificationEventArgs>? NotificationReceived;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public SessionSnapshot Session => _session;

    /// <summary>Gets the underlying client, for callers that register server-to-client handlers (agent mode).</summary>
    public IpcClient Client { get; }

    /// <inheritdoc />
    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken) =>
        Client.InvokeAsync<TRequest, TResponse>(method, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<TResponse> InvokeAsync<TResponse>(string method, CancellationToken cancellationToken) =>
        Client.InvokeAsync<TResponse>(method, cancellationToken);

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Client.NotificationReceived -= OnNotificationReceived;
        await Client.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Client.RunAsync(OnConnectedAsync, stoppingToken);

    private async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        SubscribeResponse subscription = await Client.InvokeAsync<SubscribeResponse>(IpcMethods.EventsSubscribe, cancellationToken).ConfigureAwait(false);
        ApplyStatus(subscription.Status);
        foreach (IConnectionInitializer initializer in _initializers)
        {
            await initializer.OnConnectedAsync(Client, cancellationToken).ConfigureAwait(false);
        }

        SetConnected(true);
        try
        {
            // RunAsync waits for the connection to end only after this callback returns, so watch liveness here.
            while (Client.IsConnected)
            {
                await Task.Delay(LivenessPoll, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            SetConnected(false);
        }
    }

    private void SetConnected(bool value)
    {
        if (_isConnected == value)
        {
            return;
        }

        _isConnected = value;
        _dispatcher.Post(() => ConnectionChanged?.Invoke(this, EventArgs.Empty));
    }

    private void ApplyStatus(SessionStatusDto status)
    {
        _session = status.ToSnapshot();
        _dispatcher.Post(() => SessionChanged?.Invoke(this, EventArgs.Empty));
    }

    private void OnNotificationReceived(object? sender, IpcNotificationEventArgs e)
    {
        try
        {
            switch (e.Method)
            {
                case IpcMethods.EventSessionChanged:
                    ApplyStatus(IpcJson.FromElement<SessionStatusDto>(e.Parameters));
                    break;
                case IpcMethods.EventNotification:
                    NotificationDto notification = IpcJson.FromElement<NotificationDto>(e.Parameters);
                    _dispatcher.Post(() => NotificationReceived?.Invoke(this, new NotificationEventArgs(notification)));
                    break;
                default:
                    LogUnknownNotification(_logger, e.Method);
                    break;
            }
        }
        catch (JsonException ex)
        {
            LogBadNotification(_logger, ex, e.Method);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignoring unknown notification {Method}.")]
    private static partial void LogUnknownNotification(ILogger logger, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Method} had an unexpected shape.")]
    private static partial void LogBadNotification(ILogger logger, Exception exception, string method);
}
