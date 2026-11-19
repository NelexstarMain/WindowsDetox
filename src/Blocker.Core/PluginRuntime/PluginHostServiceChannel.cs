using Blocker.Contracts;
using Blocker.Core.Ipc;

namespace Blocker.Core.PluginRuntime;

/// <summary>
/// What the plugin host asks of the service on behalf of plugins (starting sessions, toasts). Implemented over the host's IPC client in
/// production, faked in tests. Every request names the plugin; the service re-checks that plugin's manifest.
/// </summary>
public interface IPluginHostServiceChannel
{
    /// <summary>Starts a session.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome; failures of the channel itself are reported as <c>Started = false</c>.</returns>
    ValueTask<PluginStartSessionResponse> StartSessionAsync(PluginStartSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Shows a toast.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    ValueTask<PluginNotifyResponse> NotifyAsync(PluginNotifyRequest request, CancellationToken cancellationToken);
}

/// <summary>Channel over the host's pipe to the service. A broken pipe, a timeout or a refusal turns into a negative result, never into an exception.</summary>
/// <param name="client">The host's IPC client.</param>
public sealed class IpcPluginHostServiceChannel(IpcClient client) : IPluginHostServiceChannel
{
    private const string Unavailable = "The Windows Detox service is not available.";

    /// <inheritdoc/>
    public async ValueTask<PluginStartSessionResponse> StartSessionAsync(PluginStartSessionRequest request, CancellationToken cancellationToken) =>
        await CallAsync<PluginStartSessionRequest, PluginStartSessionResponse>(IpcMethods.PluginHostStartSession, request, new(false, Unavailable), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<PluginNotifyResponse> NotifyAsync(PluginNotifyRequest request, CancellationToken cancellationToken) =>
        await CallAsync<PluginNotifyRequest, PluginNotifyResponse>(IpcMethods.PluginHostNotify, request, new(false), cancellationToken).ConfigureAwait(false);

    private async ValueTask<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request, TResponse failure, CancellationToken cancellationToken)
    {
        try
        {
            return await client.InvokeAsync<TRequest, TResponse>(method, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcException or IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            return failure;
        }
    }
}

/// <summary>Channel that refuses everything; used where no service is connected (package evaluation, tests).</summary>
public sealed class UnavailablePluginHostServiceChannel : IPluginHostServiceChannel
{
    private const string Unavailable = "The Windows Detox service is not available.";

    /// <summary>Gets the shared instance.</summary>
    public static UnavailablePluginHostServiceChannel Instance { get; } = new();

    /// <inheritdoc/>
    public ValueTask<PluginStartSessionResponse> StartSessionAsync(PluginStartSessionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PluginStartSessionResponse(false, Unavailable));

    /// <inheritdoc/>
    public ValueTask<PluginNotifyResponse> NotifyAsync(PluginNotifyRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PluginNotifyResponse(false));
}
