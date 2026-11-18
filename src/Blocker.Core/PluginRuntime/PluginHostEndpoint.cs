using System.Text.Json;
using System.Threading.Channels;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Plugins;
using Blocker.Plugin.Testing.Runner;
using Blocker.Sdk;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>
/// The plugin host's side of the <c>host.*</c> protocol: connects to the service, registers (<c>pluginHost.register</c>), answers the
/// service's requests by calling a <see cref="PluginHostRuntime"/> or the install pipeline, and reports faulted plugins and render
/// requests upward. Lives in Core so the whole protocol can be tested in-process; <c>Blocker.PluginHost</c> only wires it up.
/// </summary>
public sealed partial class PluginHostEndpoint
{
    private const int MaxReportedErrors = 50;
    private const int MaxErrorLength = 500;

    private readonly IpcClient _client;
    private readonly PluginHostRuntime _runtime;
    private readonly ILogger<PluginHostEndpoint> _logger;
    private readonly string? _token;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pendingRenders = [];
    private Channel<(string Method, JsonElement Payload)>? _outbox;

    /// <summary>Creates the endpoint; nothing connects until <see cref="RunAsync"/>.</summary>
    /// <param name="client">Client of the plugin-host pipe.</param>
    /// <param name="runtime">The plugin runtime to serve.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="token">Launch token from <c>--token</c>.</param>
    public PluginHostEndpoint(IpcClient client, PluginHostRuntime runtime, ILogger<PluginHostEndpoint> logger, string? token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _runtime = runtime;
        _logger = logger;
        _token = token;
    }

    /// <summary>Connects, registers and serves until the pipe closes or the service sends <c>host.shutdown</c>; then shuts the runtime down.</summary>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns><see langword="false"/> when the service refused the registration.</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<IDisposable> registrations = RegisterHandlers(shutdown);
        var outbox = Channel.CreateBounded<(string Method, JsonElement Payload)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _outbox = outbox;
        Task pump = Task.CompletedTask;
        _runtime.PluginFaulted += OnPluginFaulted;
        _runtime.RenderRequested += OnRenderRequested;
        try
        {
            PluginHostRegisterResponse reply = await _client.InvokeAsync<PluginHostRegisterRequest, PluginHostRegisterResponse>(
                IpcMethods.PluginHostRegister, new PluginHostRegisterRequest(PluginHostProtocol.Version, _token), cancellationToken).ConfigureAwait(false);
            if (!reply.Accepted)
            {
                LogRefused(_logger);
                return false;
            }

            pump = Task.Run(() => PumpAsync(outbox.Reader, cancellationToken), CancellationToken.None);
            await Task.WhenAny(_client.WaitForDisconnectAsync(cancellationToken), shutdown.Task).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _runtime.PluginFaulted -= OnPluginFaulted;
            _runtime.RenderRequested -= OnRenderRequested;
            outbox.Writer.TryComplete();
            await pump.ConfigureAwait(false);
            await _runtime.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (IDisposable registration in registrations)
            {
                registration.Dispose();
            }
        }
    }

    private static PluginInstallStageDto Map(PluginInstallStage stage) => stage switch
    {
        PluginInstallStage.Package => PluginInstallStageDto.Package,
        PluginInstallStage.Manifest => PluginInstallStageDto.Manifest,
        PluginInstallStage.Policy => PluginInstallStageDto.Policy,
        PluginInstallStage.Compile => PluginInstallStageDto.Compile,
        PluginInstallStage.Load => PluginInstallStageDto.Load,
        PluginInstallStage.Tests => PluginInstallStageDto.Tests,
        _ => PluginInstallStageDto.Ready,
    };

    private static HostPluginState Map(PluginRuntimeState state) => state switch
    {
        PluginRuntimeState.Running => HostPluginState.Running,
        PluginRuntimeState.Faulted => HostPluginState.Faulted,
        _ => HostPluginState.Failed,
    };

    private static PluginInstallReportDto ToDto(PluginInstallReport report)
    {
        PluginManifest? manifest = report.Manifest;
        PluginTestReport? tests = report.TestReport;
        return new PluginInstallReportDto(
            Map(report.Stage),
            [.. report.Errors.Take(MaxReportedErrors).Select(e => PluginText.Printable(e, MaxErrorLength))],
            manifest?.Id.Value,
            manifest?.Name,
            manifest?.Version.ToString(),
            manifest?.Author,
            [.. manifest?.Capabilities.Select(PluginCapabilities.ToManifestName) ?? []],
            tests?.Passed ?? 0,
            tests?.Results.Count ?? 0);
    }

    private static OperationResponse ToResponse(UiActionResult result) => result switch
    {
        UiActionResult.Delivered => new OperationResponse(true, []),
        UiActionResult.NotFound => new OperationResponse(false, ["Unknown contribution."]),
        UiActionResult.Invalid => new OperationResponse(false, ["Invalid action."]),
        _ => new OperationResponse(false, ["The plugin could not handle the action."]),
    };

    private List<IDisposable> RegisterHandlers(TaskCompletionSource shutdown) =>
    [
        Handle<LoadPluginsRequest, LoadPluginsResponse>(IpcMethods.HostLoadPlugins, LoadPluginsAsync),
        Handle<EvaluatePackageRequest, PluginInstallReportDto>(IpcMethods.HostEvaluatePackage, EvaluatePackageAsync),
        Handle<InterceptProcessRequest, InterceptResponse>(IpcMethods.HostInterceptProcess, (r, ct) => _runtime.InterceptProcessAsync(r.Process, ct)),
        Handle<InterceptWindowRequest, InterceptResponse>(IpcMethods.HostInterceptWindow, (r, ct) => _runtime.InterceptWindowAsync(r.Window, ct)),
        Handle<EvaluateLockRequest, EvaluateLockResponse>(IpcMethods.HostEvaluateLock, (r, ct) => _runtime.EvaluateLockAsync(r.Action, ct)),
        Handle<GetContributionsRequest, GetContributionsResponse>(IpcMethods.HostRenderSlot, (r, ct) => _runtime.RenderSlotAsync(r.Slot, ct)),
        Handle<SendActionRequest, OperationResponse>(IpcMethods.HostSendUiAction, async (r, ct) =>
            ToResponse(await _runtime.SendUiActionAsync(r.PluginId, r.ContributionId, r.Action, ct).ConfigureAwait(false))),
        Handle<InvokePluginMethodRequest, InvokePluginMethodResponse>(IpcMethods.HostInvokePluginMethod, async (r, ct) =>
            new InvokePluginMethodResponse(await _runtime.InvokePluginMethodAsync(r.PluginId, r.Method, r.PayloadJson, ct).ConfigureAwait(false))),
        Handle<SessionChangedRequest, EmptyDto>(IpcMethods.HostSessionChanged, (r, _) =>
        {
            _runtime.UpdateSession(r.Status.ToSnapshot(), r.VerifiedUtcNow);
            return ValueTask.FromResult(new EmptyDto());
        }),
        Handle<HostEventDto, EmptyDto>(IpcMethods.HostPublishEvent, async (r, ct) =>
        {
            await _runtime.PublishEventAsync(r.ToEvent() ?? throw new IpcException(IpcErrorCode.ValidationFailed, "Invalid event."), ct).ConfigureAwait(false);
            return new EmptyDto();
        }),
        Handle<EmptyDto, EmptyDto>(IpcMethods.HostShutdown, (_, _) =>
        {
            shutdown.TrySetResult();
            return ValueTask.FromResult(new EmptyDto());
        }),
    ];

    private IDisposable Handle<TRequest, TResponse>(string method, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler) =>
        _client.Register(method, async (parameters, cancellationToken) =>
        {
            TRequest request;
            try
            {
                request = IpcJson.FromElement<TRequest>(parameters);
            }
            catch (JsonException)
            {
                throw new IpcException(IpcErrorCode.InvalidRequest, "Invalid parameters.");
            }

            return IpcJson.ToElement(await handler(request, cancellationToken).ConfigureAwait(false));
        });

    private async ValueTask<LoadPluginsResponse> LoadPluginsAsync(LoadPluginsRequest request, CancellationToken cancellationToken)
    {
        _runtime.UpdateSession(request.Session.ToSnapshot(), request.VerifiedUtcNow);
        IReadOnlyList<PluginRuntimeStatus> statuses = await _runtime.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new LoadPluginsResponse([.. statuses.Select(s => new HostPluginStatusDto(s.Id.Value, s.Info?.Name, s.Info?.Version.ToString(), Map(s.State), s.Error))]);
    }

    private static async ValueTask<PluginInstallReportDto> EvaluatePackageAsync(EvaluatePackageRequest request, CancellationToken cancellationToken)
    {
        byte[] package;
        try
        {
            package = Convert.FromBase64String(request.PackageBase64);
        }
        catch (FormatException)
        {
            throw new IpcException(IpcErrorCode.ValidationFailed, "The package is not valid base64.");
        }

        await using var zip = new MemoryStream(package, writable: false);
        return ToDto(await PluginInstallPipeline.EvaluateAsync(zip, cancellationToken).ConfigureAwait(false));
    }

    private void OnPluginFaulted(object? sender, PluginFaultedEventArgs e) =>
        _outbox?.Writer.TryWrite((IpcMethods.PluginHostPluginFaulted, IpcJson.ToElement(new PluginFaultedNotification(e.PluginId.Value, e.Reason))));

    // Render requests are coalesced per contribution: one pending notification covers any number of requests before it is sent.
    private void OnRenderRequested(object? sender, RenderRequestedEventArgs e)
    {
        string key = $"{e.PluginId.Value}/{e.ContributionId}";
        lock (_gate)
        {
            if (!_pendingRenders.Add(key))
            {
                return;
            }
        }

        _outbox?.Writer.TryWrite((IpcMethods.PluginHostRenderRequested, IpcJson.ToElement(new RenderRequestedNotification(e.PluginId.Value, e.ContributionId))));
    }

    private async Task PumpAsync(ChannelReader<(string Method, JsonElement Payload)> reader, CancellationToken cancellationToken)
    {
        await foreach ((string method, JsonElement payload) in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (method == IpcMethods.PluginHostRenderRequested)
            {
                RenderRequestedNotification note = IpcJson.FromElement<RenderRequestedNotification>(payload);
                lock (_gate)
                {
                    _pendingRenders.Remove($"{note.PluginId}/{note.ContributionId}");
                }
            }

            try
            {
                await _client.NotifyAsync(method, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                LogNotifyFailed(_logger, method, ex.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The service refused the plugin host registration.")]
    private static partial void LogRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notification {Method} could not be sent ({Reason}).")]
    private static partial void LogNotifyFailed(ILogger logger, string method, string reason);
}
