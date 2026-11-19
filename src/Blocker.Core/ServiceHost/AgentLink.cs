using Blocker.Contracts;
using Blocker.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.ServiceHost;

/// <summary>Version of the agent protocol (<c>agent.*</c> methods) the service speaks.</summary>
public static class AgentProtocol
{
    /// <summary>Current protocol version.</summary>
    public const int Version = 1;
}

/// <summary>What the plugin-host request handlers need from the user-session agent; implemented by <see cref="AgentLink"/>, faked in tests.</summary>
internal interface IAgentToasts
{
    /// <summary>Gets whether an agent is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Shows a toast; skipped when there is no agent.</summary>
    /// <param name="notification">The toast.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the agent answered or the call failed.</returns>
    ValueTask NotifyAsync(NotificationDto notification, CancellationToken cancellationToken);
}

/// <summary>
/// The service's view of the user-session agent: the connection of the agent that registered last. The service asks it to close windows
/// gracefully and to show toasts; without an agent (nobody logged on, agent not started) both are skipped and the caller falls back.
/// </summary>
/// <param name="startTimeOf">Returns the creation time of a running process, or <see langword="null"/> when it is gone.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class AgentLink(Func<int, long?> startTimeOf, ILogger<AgentLink> logger) : IAgentToasts
{
    private static readonly TimeSpan CloseWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(10);

    private IpcPeer? _peer;

    /// <summary>Gets whether an agent is connected.</summary>
    public bool IsConnected => Volatile.Read(ref _peer) is not null;

    /// <summary>Makes <paramref name="peer"/> the agent; it is forgotten again when it disconnects.</summary>
    /// <param name="peer">The agent's connection.</param>
    public void Register(IpcPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        Volatile.Write(ref _peer, peer);
        _ = peer.Disconnected.ContinueWith(
            _ => Interlocked.CompareExchange(ref _peer, null, peer),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Asks the agent to close the windows of a process; <see langword="false"/> when there is no agent, the call failed or the process stayed.</summary>
    /// <param name="processId">Process id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the process exited.</returns>
    public async Task<bool> CloseProcessAsync(int processId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _peer) is not { } peer || startTimeOf(processId) is not { } startTicks)
        {
            return false;
        }

        try
        {
            var request = new CloseProcessRequest(processId, startTicks, (int)CloseWindow.TotalMilliseconds);
            CloseProcessResponse response = IpcJson.FromElement<CloseProcessResponse>(
                await peer.InvokeAsync(IpcMethods.AgentCloseProcess, IpcJson.ToElement(request), CallBudget, cancellationToken).ConfigureAwait(false));
            return response.Closed;
        }
        catch (Exception ex) when (ex is IpcException or IOException or TimeoutException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            LogAgentCallFailed(logger, ex.GetType().Name, IpcMethods.AgentCloseProcess);
            return false;
        }
    }

    /// <summary>Shows a toast through the agent; skipped when there is no agent.</summary>
    /// <param name="notification">The toast.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the agent answered or the call failed.</returns>
    public async ValueTask NotifyAsync(NotificationDto notification, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _peer) is not { } peer)
        {
            return;
        }

        try
        {
            _ = await peer.InvokeAsync(IpcMethods.AgentNotify, IpcJson.ToElement(notification), CallBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcException or IOException or TimeoutException or ObjectDisposedException)
        {
            LogAgentCallFailed(logger, ex.GetType().Name, IpcMethods.AgentNotify);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent call {Method} failed ({Reason}).")]
    private static partial void LogAgentCallFailed(ILogger logger, string reason, string method);
}
