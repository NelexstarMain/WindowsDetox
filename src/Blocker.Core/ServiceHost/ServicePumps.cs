using System.Threading.Channels;
using Blocker.Contracts;
using Blocker.Core.Abstractions;
using Blocker.Core.Ipc;
using Blocker.Core.Orchestration;
using Blocker.Sdk.Events;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.ServiceHost;

/// <summary>
/// Hands core events to the plugin host without ever making the publisher wait: the session state machine and the coordinator publish from
/// inside enforcement, a slow plugin host must not delay a block. Events are queued (oldest dropped when full) and delivered in order by
/// one background task.
/// </summary>
internal sealed partial class PluginEventForwarder : IEventPublisher, IAsyncDisposable
{
    private readonly IPluginHostGateway _gateway;
    private readonly ILogger<PluginEventForwarder> _logger;
    private readonly Channel<IBlockerEvent> _queue = Channel.CreateBounded<IBlockerEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public PluginEventForwarder(IPluginHostGateway gateway, ILogger<PluginEventForwarder> logger)
    {
        _gateway = gateway;
        _logger = logger;
        _loop = Task.Run(RunAsync);
    }

    public ValueTask PublishAsync(IBlockerEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _ = _queue.Writer.TryWrite(evt);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _ = _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (IBlockerEvent evt in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await _gateway.PublishEventAsync(evt, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogForwardFailed(_logger, ex, evt.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            LogForwarderStopped(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forwarding {EventType} to the plugin host failed.")]
    private static partial void LogForwardFailed(ILogger logger, Exception exception, string eventType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event forwarding stopped.")]
    private static partial void LogForwarderStopped(ILogger logger);
}

/// <summary>Turns coordinator notifications into toasts on the agent and pushed notifications for subscribed clients, off the coordinator's thread.</summary>
internal sealed partial class NotificationPump : IAsyncDisposable
{
    private const int MaxMessageLength = 500;

    private readonly AgentLink _agent;
    private readonly IpcServer _server;
    private readonly ILogger<NotificationPump> _logger;
    private readonly Channel<NotificationDto> _queue = Channel.CreateBounded<NotificationDto>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public NotificationPump(AgentLink agent, IpcServer server, ILogger<NotificationPump> logger)
    {
        _agent = agent;
        _server = server;
        _logger = logger;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Maps a coordinator notification to its wire form.</summary>
    public static NotificationDto Map(EnforcementNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        (string title, NotificationLevel level) = notification.Kind switch
        {
            EnforcementNotificationKind.PassEndingSoon => ("Emergency pass ending", NotificationLevel.Warning),
            EnforcementNotificationKind.PassEnded => ("Emergency pass ended", NotificationLevel.Info),
            EnforcementNotificationKind.SessionEnded => ("Session ended", NotificationLevel.Info),
            _ => ("Protection degraded", NotificationLevel.Warning),
        };
        string text = notification.Text.Length <= MaxMessageLength ? notification.Text : notification.Text[..MaxMessageLength];
        return new NotificationDto(title, text, level);
    }

    public void Enqueue(EnforcementNotification notification) => _ = _queue.Writer.TryWrite(Map(notification));

    public async ValueTask DisposeAsync()
    {
        _ = _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (NotificationDto notification in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await _agent.NotifyAsync(notification, _stop.Token).ConfigureAwait(false);
                    await _server.PublishAsync(IpcMethods.EventNotification, notification, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogDeliveryFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            LogPumpStopped(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering a notification failed.")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notification delivery stopped.")]
    private static partial void LogPumpStopped(ILogger logger);
}
