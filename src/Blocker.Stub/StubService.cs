using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;
using Microsoft.Extensions.Logging;

namespace Blocker.Stub;

/// <summary>The stub's view of the Blocker service.</summary>
internal interface IStubService
{
    /// <summary>Raised on the UI thread when the service pushes a new session state.</summary>
    event EventHandler<SessionSnapshot>? SessionChanged;

    /// <summary>Asks why the block screen was shown and subscribes to session updates.</summary>
    /// <param name="blockId">Identifier of the block.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was blocked and the current session state.</returns>
    ValueTask<BlockInfoResponse> GetBlockInfoAsync(string blockId, CancellationToken cancellationToken);

    /// <summary>Starts an emergency pass.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the new state.</returns>
    ValueTask<EmergencyPassResponse> UseEmergencyPassAsync(CancellationToken cancellationToken);

    /// <summary>Fetches the plugin block-screen contributions.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The contributions.</returns>
    ValueTask<GetContributionsResponse> GetContributionsAsync(CancellationToken cancellationToken);

    /// <summary>Forwards an interaction to a plugin contribution.</summary>
    /// <param name="pluginId">Owning plugin.</param>
    /// <param name="contributionId">Contribution id.</param>
    /// <param name="action">The interaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the plugin handled it.</returns>
    ValueTask SendActionAsync(string pluginId, string contributionId, UiAction action, CancellationToken cancellationToken);
}

/// <summary>A steady tick on the UI thread.</summary>
internal interface IStubTicker
{
    /// <summary>Raised about four times per second.</summary>
    event EventHandler? Tick;
}

/// <summary>Ticker backed by a <see cref="DispatcherTimer"/>; create it on the UI thread.</summary>
internal sealed class DispatcherStubTicker : IStubTicker
{
    private readonly DispatcherTimer _timer;

    /// <summary>Creates and starts the ticker.</summary>
    public DispatcherStubTicker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        _timer.Start();
    }

    /// <inheritdoc />
    public event EventHandler? Tick;
}

/// <summary><see cref="IStubService"/> over the service pipe. Short-lived: one connection for the life of the window.</summary>
internal sealed partial class IpcStubService : IStubService, IAsyncDisposable
{
    private readonly IpcClient _client;
    private readonly ILogger<IpcStubService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="client">Client of the service pipe.</param>
    /// <param name="logger">Logger.</param>
    public IpcStubService(IpcClient client, ILogger<IpcStubService> logger)
    {
        _client = client;
        _logger = logger;
        _client.NotificationReceived += OnNotification;
    }

    /// <inheritdoc />
    public event EventHandler<SessionSnapshot>? SessionChanged;

    /// <inheritdoc />
    public async ValueTask<BlockInfoResponse> GetBlockInfoAsync(string blockId, CancellationToken cancellationToken)
    {
        BlockInfoResponse info = await _client.InvokeAsync<BlockInfoRequest, BlockInfoResponse>(IpcMethods.StubGetBlockInfo, new BlockInfoRequest(blockId), cancellationToken);
        _ = await _client.InvokeAsync<SubscribeResponse>(IpcMethods.EventsSubscribe, cancellationToken);
        return info;
    }

    /// <inheritdoc />
    public ValueTask<EmergencyPassResponse> UseEmergencyPassAsync(CancellationToken cancellationToken) =>
        _client.InvokeAsync<EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, cancellationToken);

    /// <inheritdoc />
    public ValueTask<GetContributionsResponse> GetContributionsAsync(CancellationToken cancellationToken) =>
        _client.InvokeAsync<GetContributionsRequest, GetContributionsResponse>(IpcMethods.UiGetContributions, new GetContributionsRequest(UiSlot.BlockScreen), cancellationToken);

    /// <inheritdoc />
    public async ValueTask SendActionAsync(string pluginId, string contributionId, UiAction action, CancellationToken cancellationToken) =>
        _ = await _client.InvokeAsync<SendActionRequest, EmptyDto>(IpcMethods.UiSendAction, new SendActionRequest(pluginId, contributionId, action), cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.NotificationReceived -= OnNotification;
        return _client.DisposeAsync();
    }

    private void OnNotification(object? sender, IpcNotificationEventArgs e)
    {
        if (e.Method != IpcMethods.EventSessionChanged)
        {
            return;
        }

        try
        {
            SessionSnapshot snapshot = IpcJson.FromElement<SessionStatusDto>(e.Parameters).ToSnapshot();
            Application.Current?.Dispatcher.BeginInvoke(() => SessionChanged?.Invoke(this, snapshot));
        }
        catch (JsonException ex)
        {
            LogBadNotification(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session notification had an unexpected shape.")]
    private static partial void LogBadNotification(ILogger logger, Exception exception);
}
