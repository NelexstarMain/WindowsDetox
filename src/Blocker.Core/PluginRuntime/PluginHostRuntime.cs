using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Events;
using Blocker.Core.Plugins;
using Blocker.Core.Resilience;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>State of a plugin inside <see cref="PluginHostRuntime"/>.</summary>
public enum PluginRuntimeState
{
    /// <summary>Loaded and serving callbacks.</summary>
    Running,

    /// <summary>Its circuit breaker tripped; its contributions were removed.</summary>
    Faulted,

    /// <summary>It could not be built, loaded or initialized.</summary>
    Failed,
}

/// <summary>What became of one enabled plugin.</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Info">Identity and capabilities, once the manifest was read.</param>
/// <param name="State">Current state.</param>
/// <param name="Error">Why the plugin failed or faulted.</param>
public sealed record PluginRuntimeStatus(PluginId Id, PluginInfo? Info, PluginRuntimeState State, string? Error);

/// <summary>Limits and budgets of <see cref="PluginHostRuntime"/>.</summary>
public sealed record PluginRuntimeOptions
{
    /// <summary>Gets the thresholds of every plugin's circuit breaker (default: 50 ms budget, trips after 5 consecutive failures).</summary>
    public CircuitBreakerOptions Breaker { get; init; } = new();

    /// <summary>Gets the factor applied to the callback budget for <see cref="IBlockerPlugin.InitializeAsync"/>.</summary>
    public int InitializeBudgetFactor { get; init; } = 20;

    /// <summary>Gets how long <see cref="IBlockerPlugin.ShutdownAsync"/> may take per plugin.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the storage quota of each plugin, in bytes.</summary>
    public long StorageQuotaBytes { get; init; } = FileBackedPluginStorage.DefaultQuotaBytes;

    /// <summary>Gets the largest serialized UI tree of one contribution, in bytes.</summary>
    public int MaxContributionBytes { get; init; } = 128 * 1024;

    /// <summary>Gets the largest total of all trees in one slot response, in bytes (the service accepts 256 KiB frames from the host).</summary>
    public int MaxSlotResponseBytes { get; init; } = 224 * 1024;

    /// <summary>Gets the time budget of <see cref="IBlockerPlugin.InitializeAsync"/>.</summary>
    public TimeSpan InitializeBudget => TimeSpan.FromTicks(Breaker.Budget.Ticks * InitializeBudgetFactor);
}

/// <summary>Outcome of forwarding a user interaction to a plugin.</summary>
public enum UiActionResult
{
    /// <summary>The plugin's handler ran.</summary>
    Delivered,

    /// <summary>No such plugin or contribution.</summary>
    NotFound,

    /// <summary>The interaction is malformed.</summary>
    Invalid,

    /// <summary>The handler failed, timed out or the plugin is faulted.</summary>
    Failed,
}

/// <summary>Raised when a plugin's circuit breaker tripped.</summary>
/// <param name="PluginId">The faulted plugin.</param>
/// <param name="Reason">Why it faulted.</param>
public sealed class PluginFaultedEventArgs(PluginId PluginId, string Reason) : EventArgs
{
    /// <summary>Gets the faulted plugin.</summary>
    public PluginId PluginId { get; } = PluginId;

    /// <summary>Gets why it faulted.</summary>
    public string Reason { get; } = Reason;
}

/// <summary>Raised when a contribution asked for another render.</summary>
/// <param name="PluginId">Owning plugin.</param>
/// <param name="ContributionId">Contribution within the plugin.</param>
public sealed class RenderRequestedEventArgs(PluginId PluginId, string ContributionId) : EventArgs
{
    /// <summary>Gets the owning plugin.</summary>
    public PluginId PluginId { get; } = PluginId;

    /// <summary>Gets the contribution.</summary>
    public string ContributionId { get; } = ContributionId;
}

/// <summary>
/// Runs the enabled plugins of a <see cref="PluginStore"/> inside the plugin host: re-validates and compiles each package (no tests;
/// those run only at install), loads it into a collectible context, and gives it a production <see cref="IPluginContext"/>.
/// Every call into plugin code goes through the plugin's <see cref="PluginCircuitBreaker"/> on a thread-pool thread; a tripped
/// breaker marks the plugin <see cref="PluginRuntimeState.Faulted"/> and removes its contributions. Failures fall back safely:
/// interceptors abstain, lock votes allow, renders disappear.
/// </summary>
public sealed partial class PluginHostRuntime : IAsyncDisposable
{
    private readonly PluginStore _store;
    private readonly HostEventBus _bus;
    private readonly VerifiedTimeProvider _clock;
    private readonly ILogger<PluginHostRuntime> _logger;
    private readonly PluginRuntimeOptions _options;
    private readonly HostSessionInfo _session = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<PluginId, LoadedPlugin> _plugins = [];
    private readonly Dictionary<PluginId, PluginRuntimeStatus> _failed = [];

    /// <summary>Creates a runtime; nothing is loaded until <see cref="LoadAsync"/>.</summary>
    /// <param name="store">Installed plugins.</param>
    /// <param name="bus">Event bus shared with the rest of the host; plugin subscriptions live on it.</param>
    /// <param name="clock">The plugin clock; also used for breakers, timeouts and tick timers. Use the same instance for <paramref name="bus"/>.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Limits; defaults when <see langword="null"/>.</param>
    public PluginHostRuntime(PluginStore store, HostEventBus bus, VerifiedTimeProvider clock, ILogger<PluginHostRuntime> logger, PluginRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _bus = bus;
        _clock = clock;
        _logger = logger;
        _options = options ?? new PluginRuntimeOptions();
    }

    /// <summary>Raised (on a thread-pool thread) when a plugin's circuit breaker tripped.</summary>
    public event EventHandler<PluginFaultedEventArgs>? PluginFaulted;

    /// <summary>Raised when a contribution calls <see cref="IUiSurface.RequestRender"/>.</summary>
    public event EventHandler<RenderRequestedEventArgs>? RenderRequested;

    /// <summary>Gets every enabled plugin and its state, in ordinal id order.</summary>
    public IReadOnlyList<PluginRuntimeStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _plugins.Values.Select(p => new PluginRuntimeStatus(p.Id, p.Info, p.IsFaulted ? PluginRuntimeState.Faulted : PluginRuntimeState.Running, p.FaultReason)),
                    .. _failed.Values,
                ];
            }
        }
    }

    /// <summary>Records the session state and re-anchors the plugin clock on the verified time.</summary>
    /// <param name="snapshot">The session state plugins see through <see cref="ISessionInfo"/>.</param>
    /// <param name="verifiedUtcNow">Network-verified UTC time right now.</param>
    public void UpdateSession(SessionSnapshot snapshot, DateTimeOffset verifiedUtcNow)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _clock.Anchor(verifiedUtcNow);
        _session.Set(snapshot);
    }

    /// <summary>
    /// Makes the running set match the store: unloads plugins that were removed or disabled, loads newly enabled ones, reloads
    /// those whose package changed and retries those that failed to load. Faulted plugins stay faulted until their package changes.
    /// </summary>
    /// <param name="cancellationToken">Cancels loading.</param>
    /// <returns>The state of every enabled plugin.</returns>
    public async ValueTask<IReadOnlyList<PluginRuntimeStatus>> LoadAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<StoredPlugin> wanted = [.. (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).Where(p => p.Enabled)];
            HashSet<PluginId> wantedIds = [.. wanted.Select(p => p.Id)];
            foreach (LoadedPlugin gone in Snapshot().Where(p => !wantedIds.Contains(p.Id)))
            {
                await UnloadAsync(gone, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                foreach (PluginId stale in _failed.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
                {
                    _failed.Remove(stale);
                }
            }

            foreach (StoredPlugin stored in wanted)
            {
                await EnsureLoadedAsync(stored.Id, cancellationToken).ConfigureAwait(false);
            }

            return Statuses;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Asks every process interceptor; the first block (in plugin id order) wins. Failures abstain.</summary>
    /// <param name="process">The new process.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict.</returns>
    public ValueTask<InterceptResponse> InterceptProcessAsync(ProcessDescriptor process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        return InterceptAsync<IProcessInterceptor>((_, interceptor, token) => interceptor.OnProcessStartingAsync(process, token), cancellationToken);
    }

    /// <summary>Asks every window interceptor; plugins without the window-title capability get the window without its title.</summary>
    /// <param name="window">The newly activated window.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The verdict.</returns>
    public ValueTask<InterceptResponse> InterceptWindowAsync(WindowDescriptor window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        return InterceptAsync<IWindowInterceptor>(
            (plugin, interceptor, token) => interceptor.OnWindowActivatedAsync(plugin.Info.Has(PluginCapability.WindowTitles) ? window : window with { Title = null }, token),
            cancellationToken);
    }

    /// <summary>Collects the vetoes of the lock-policy contributors. Failures vote allow.</summary>
    /// <param name="action">The voluntary action.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The vetoes in plugin id order.</returns>
    public async ValueTask<EvaluateLockResponse> EvaluateLockAsync(LockAction action, CancellationToken cancellationToken)
    {
        List<Task<LockVetoDto?>> votes = [];
        foreach (LoadedPlugin plugin in Active())
        {
            foreach (ExtensionEntry entry in plugin.Extensions.Snapshot().Where(e => e.Extension is ILockPolicyContributor))
            {
                votes.Add(VoteAsync(plugin, (ILockPolicyContributor)entry.Extension, action, cancellationToken));
            }
        }

        LockVetoDto?[] results = await Task.WhenAll(votes).ConfigureAwait(false);
        return new EvaluateLockResponse([.. results.OfType<LockVetoDto>()]);
    }

    /// <summary>Renders every contribution of <paramref name="slot"/>; contributions that fail or are invalid are left out.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The serialized trees in plugin id and registration order.</returns>
    public async ValueTask<GetContributionsResponse> RenderSlotAsync(UiSlot slot, CancellationToken cancellationToken)
    {
        List<Task<UiContributionDto?>> renders = [];
        foreach (LoadedPlugin plugin in Active())
        {
            foreach (ExtensionEntry entry in plugin.Extensions.Snapshot().Where(e => ContributionIds.Contributes(slot, e.Extension)))
            {
                renders.Add(RenderOneAsync(plugin, entry, slot, cancellationToken));
            }
        }

        List<UiContributionDto> items = [];
        int total = 0;
        foreach (UiContributionDto? dto in await Task.WhenAll(renders).ConfigureAwait(false))
        {
            if (dto is null)
            {
                continue;
            }

            int size = Encoding.UTF8.GetByteCount(dto.Json);
            if (total + size > _options.MaxSlotResponseBytes)
            {
                LogSlotBudget(_logger, dto.PluginId, slot);
                continue;
            }

            total += size;
            items.Add(dto);
        }

        return new GetContributionsResponse(slot, items);
    }

    /// <summary>Forwards a user interaction to the contribution it was meant for.</summary>
    /// <param name="pluginId">Owning plugin.</param>
    /// <param name="contributionId">Contribution id from <see cref="RenderSlotAsync"/>.</param>
    /// <param name="action">The interaction; validated here.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    public async ValueTask<UiActionResult> SendUiActionAsync(string pluginId, string contributionId, UiAction action, CancellationToken cancellationToken)
    {
        if (!PluginId.TryCreate(pluginId, out PluginId id)
            || FindActive(id) is not { } plugin
            || !ContributionIds.TryParse(contributionId, out UiSlot slot, out int index)
            || plugin.Extensions.Snapshot().FirstOrDefault(e => e.Index == index && ContributionIds.Contributes(slot, e.Extension)) is not { Extension: IUiContribution contribution })
        {
            return UiActionResult.NotFound;
        }

        if (!UiActionValidator.IsValid(action))
        {
            return UiActionResult.Invalid;
        }

        var context = new UiRenderContext(_session.Current, new ContributionSurface(this, id, contributionId));
        BreakerResult<bool> result = await plugin.GuardAsync(token => contribution.OnActionAsync(action, context, token), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? UiActionResult.Delivered : UiActionResult.Failed;
    }

    /// <summary>Calls a method a plugin registered with its IPC registry.</summary>
    /// <param name="pluginId">Owning plugin.</param>
    /// <param name="method">Plugin-local method name.</param>
    /// <param name="payloadJson">Request JSON, at most <see cref="BlockerSdk.MaxPayloadBytes"/> bytes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response JSON.</returns>
    /// <exception cref="IpcException">Unknown plugin or method, oversized or invalid payload, or the plugin failed.</exception>
    public async ValueTask<string> InvokePluginMethodAsync(string pluginId, string method, string payloadJson, CancellationToken cancellationToken)
    {
        if (!PluginId.TryCreate(pluginId, out PluginId id) || FindActive(id) is not { } plugin || method is null || !plugin.Ipc.Contains(method))
        {
            throw new IpcException(IpcErrorCode.NotFound, "Unknown method.");
        }

        if (payloadJson is null || Encoding.UTF8.GetByteCount(payloadJson) > BlockerSdk.MaxPayloadBytes)
        {
            throw new IpcException(IpcErrorCode.PayloadTooLarge, "Message too large.");
        }

        try
        {
            PayloadValidator.ThrowIfInvalid(payloadJson, nameof(payloadJson));
        }
        catch (ArgumentException)
        {
            throw new IpcException(IpcErrorCode.ValidationFailed, "The payload is not valid JSON.");
        }

        BreakerResult<string?> result = await plugin.GuardAsync<string?>(token => InvokeAsync(plugin, method, payloadJson, token), null, cancellationToken).ConfigureAwait(false);
        return result.Value ?? throw new IpcException(IpcErrorCode.Internal, "The plugin did not answer.");
    }

    /// <summary>Delivers a core event to the plugins' subscriptions.</summary>
    /// <param name="evt">A core event; custom plugin events are refused because only plugins may publish them.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes once the event is queued.</returns>
    public ValueTask PublishEventAsync(IBlockerEvent evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt is CustomPluginEvent
            ? throw new ArgumentException("Custom plugin events cannot be injected by the host.", nameof(evt))
            : _bus.PublishAsync(evt, cancellationToken);
    }

    /// <summary>Calls <see cref="IBlockerPlugin.ShutdownAsync"/> on every plugin (2 s cap each), removes their registrations and unloads their contexts.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when every plugin is unloaded.</returns>
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(Snapshot().Select(p => UnloadAsync(p, cancellationToken))).ConfigureAwait(false);
            lock (_gate)
            {
                _failed.Clear();
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _loadGate.Dispose();
    }

    private static async ValueTask<string?> InvokeAsync(LoadedPlugin plugin, string method, string payloadJson, CancellationToken token) =>
        await plugin.Ipc.InvokeAsync(method, payloadJson, token).ConfigureAwait(false);

    private static void RequireValidTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 64 || title.Any(char.IsControl))
        {
            throw new InvalidContributionException("The section title must be 1-64 printable characters.");
        }
    }

    private LoadedPlugin[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _plugins.Values];
        }
    }

    private LoadedPlugin[] Active() => [.. Snapshot().Where(p => !p.IsFaulted).OrderBy(p => p.Id.Value, StringComparer.Ordinal)];

    private LoadedPlugin? FindActive(PluginId id)
    {
        lock (_gate)
        {
            return _plugins.TryGetValue(id, out LoadedPlugin? plugin) && !plugin.IsFaulted ? plugin : null;
        }
    }

    private async ValueTask<InterceptResponse> InterceptAsync<TInterceptor>(
        Func<LoadedPlugin, TInterceptor, CancellationToken, ValueTask<InterceptDecision>> call,
        CancellationToken cancellationToken)
        where TInterceptor : class
    {
        List<Task<InterceptResponse>> asks = [];
        foreach (LoadedPlugin plugin in Active())
        {
            foreach (ExtensionEntry entry in plugin.Extensions.Snapshot().Where(e => e.Extension is TInterceptor))
            {
                var interceptor = (TInterceptor)entry.Extension;
                asks.Add(AskAsync(plugin, token => call(plugin, interceptor, token), cancellationToken));
            }
        }

        return (await Task.WhenAll(asks).ConfigureAwait(false)).FirstOrDefault(r => r.Block) ?? InterceptResponse.Abstain;
    }

    private static async Task<InterceptResponse> AskAsync(LoadedPlugin plugin, Func<CancellationToken, ValueTask<InterceptDecision>> call, CancellationToken cancellationToken)
    {
        BreakerResult<InterceptDecision> result = await plugin.GuardAsync(call, InterceptDecision.Abstain, cancellationToken).ConfigureAwait(false);
        return result.Value is { Verdict: InterceptVerdict.Block, Reason: { } reason }
            ? new InterceptResponse(true, PluginText.Printable(reason, 200), plugin.Id.Value)
            : InterceptResponse.Abstain;
    }

    private static async Task<LockVetoDto?> VoteAsync(LoadedPlugin plugin, ILockPolicyContributor contributor, LockAction action, CancellationToken cancellationToken)
    {
        BreakerResult<LockVote> result = await plugin.GuardAsync(token => contributor.EvaluateAsync(action, token), LockVote.Allow, cancellationToken).ConfigureAwait(false);
        return result.Value is { IsVeto: true, Reason: { } reason } ? new LockVetoDto(plugin.Id.Value, PluginText.Printable(reason, 200)) : null;
    }

    private async Task<UiContributionDto?> RenderOneAsync(LoadedPlugin plugin, ExtensionEntry entry, UiSlot slot, CancellationToken cancellationToken)
    {
        string contributionId = ContributionIds.For(slot, entry.Index);
        var context = new UiRenderContext(_session.Current, new ContributionSurface(this, plugin.Id, contributionId));
        BreakerResult<UiContributionDto?> result = await plugin.GuardAsync<UiContributionDto?>(
            token => RenderCoreAsync(plugin.Id, entry.Extension, slot, contributionId, context, token), null, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            LogRenderFailed(_logger, plugin.Id.Value, contributionId, error.GetType().Name);
        }

        return result.Value;
    }

    private async ValueTask<UiContributionDto?> RenderCoreAsync(PluginId pluginId, IPluginExtension extension, UiSlot slot, string contributionId, UiRenderContext context, CancellationToken token)
    {
        switch (slot)
        {
            case UiSlot.Page when extension is IUiPagePlugin page:
                UiNavigationItem navigation = page.Navigation;
                if (UiTreeValidator.ValidateNavigation(navigation) is { IsValid: false } invalid)
                {
                    throw new InvalidContributionException(invalid.Errors[0]);
                }

                return Serialize(pluginId, contributionId, navigation.Title, slot, await page.RenderAsync(context, token).ConfigureAwait(false));
            case UiSlot.DashboardWidget when extension is IDashboardWidgetPlugin widget:
                return Serialize(pluginId, contributionId, null, slot, await widget.RenderAsync(context, token).ConfigureAwait(false));
            case UiSlot.SettingsSection when extension is ISettingsSectionPlugin section:
                string title = section.Title;
                RequireValidTitle(title);
                return Serialize(pluginId, contributionId, title, slot, await section.RenderAsync(context, token).ConfigureAwait(false));
            case UiSlot.BlockScreen when extension is IBlockScreenPlugin block:
                return Serialize(pluginId, contributionId, null, slot, await block.RenderAsync(context, token).ConfigureAwait(false));
            default:
                return null;
        }
    }

    private UiContributionDto Serialize(PluginId pluginId, string contributionId, string? title, UiSlot slot, UiNode? tree)
    {
        if (UiTreeValidator.Validate(tree) is { IsValid: false } invalid)
        {
            throw new InvalidContributionException(invalid.Errors[0]);
        }

        if (slot != UiSlot.Page && tree is UiPage)
        {
            throw new InvalidContributionException("UiPage is only allowed in navigation pages.");
        }

        string json = UiJson.Serialize(tree!);
        return Encoding.UTF8.GetByteCount(json) > _options.MaxContributionBytes
            ? throw new InvalidContributionException($"The UI tree exceeds {_options.MaxContributionBytes} bytes.")
            : new UiContributionDto(pluginId.Value, contributionId, title, json);
    }

    private async Task EnsureLoadedAsync(PluginId id, CancellationToken cancellationToken)
    {
        byte[]? package = await _store.ReadPackageAsync(id, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            RecordFailure(id, null, "The plugin package is missing.");
            return;
        }

        string hash = Convert.ToHexString(SHA256.HashData(package));
        LoadedPlugin? existing;
        lock (_gate)
        {
            _plugins.TryGetValue(id, out existing);
        }

        if (existing is not null)
        {
            if (existing.PackageHash == hash)
            {
                return;
            }

            await UnloadAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        (LoadedPlugin? loaded, PluginRuntimeStatus? failure) = await LoadOneAsync(id, package, hash, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _failed.Remove(id);
            if (loaded is not null)
            {
                _plugins[id] = loaded;
            }
            else if (failure is not null)
            {
                _failed[id] = failure;
            }
        }
    }

    private void RecordFailure(PluginId id, PluginInfo? info, string error)
    {
        LogLoadFailed(_logger, id.Value, error);
        lock (_gate)
        {
            _failed[id] = new PluginRuntimeStatus(id, info, PluginRuntimeState.Failed, error);
        }
    }

    private async Task<(LoadedPlugin? Plugin, PluginRuntimeStatus? Failure)> LoadOneAsync(PluginId id, byte[] package, string hash, CancellationToken cancellationToken)
    {
        PluginBuildResult build;
        using (var zip = new MemoryStream(package, writable: false))
        {
            build = await PluginInstallPipeline.BuildAsync(zip, cancellationToken).ConfigureAwait(false);
        }

        if (build is not { Manifest: { } manifest, Image: { } image })
        {
            string reason = build.Failure is { } failure
                ? $"{failure.Stage}: {failure.Errors.FirstOrDefault() ?? "invalid package"}"
                : "The plugin package is invalid.";
            return (null, Failure(id, null, reason));
        }

        var info = new PluginInfo(manifest.Id, manifest.Name, manifest.Version, manifest.Author, manifest.Capabilities.ToHashSet());
        if (manifest.Id != id)
        {
            return (null, Failure(id, info, "The package id does not match its directory."));
        }

        var breaker = new PluginCircuitBreaker(id, _clock, _options.Breaker);
        var extensions = new PluginExtensionRegistry(info);
        var ipc = new PluginIpcRegistry(info);
        var plugin = new LoadedPlugin(
            info, hash, breaker, extensions, ipc, _store.OpenStorage(id, _logger, _options.StorageQuotaBytes), new PluginLoadContext(id.Value), _session, _clock, OnPluginFaulted);
        var context = new PluginHostContext(
            info, _bus.CreatePluginView(info, breaker), extensions, ipc, plugin.Storage, _session, _clock, new RateLimitedPluginLogger(id, _logger, _clock));

        string? error = await InitializeAsync(plugin, manifest, image, context, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            await DiscardAsync(plugin).ConfigureAwait(false);
            return (null, Failure(id, info, error));
        }

        await WarmUpAsync(plugin, cancellationToken).ConfigureAwait(false);
        plugin.Activate();
        LogLoaded(_logger, id.Value);
        return (plugin, null);
    }

    /// <summary>
    /// A plugin is compiled in memory, so its render code is JIT-compiled on first use. That first render easily exceeds the
    /// callback budget, and a contribution over budget is left out of the slot: the plugin's page would stay missing until the next
    /// refresh. Render each contribution once here, under the initialization budget, and discard the result. Failures are only logged:
    /// the real renders still run under the plugin's own circuit breaker.
    /// </summary>
    private async Task WarmUpAsync(LoadedPlugin plugin, CancellationToken cancellationToken)
    {
        var warmUp = new PluginCircuitBreaker(plugin.Id, _clock, _options.Breaker with { Budget = _options.InitializeBudget });
        foreach (UiSlot slot in Enum.GetValues<UiSlot>())
        {
            foreach (ExtensionEntry entry in plugin.Extensions.Snapshot().Where(e => ContributionIds.Contributes(slot, e.Extension)))
            {
                string contributionId = ContributionIds.For(slot, entry.Index);
                var context = new UiRenderContext(_session.Current, new ContributionSurface(this, plugin.Id, contributionId));
                BreakerResult<UiContributionDto?> result = await warmUp.ExecuteAsync<UiContributionDto?>(
                    token => RenderCoreAsync(plugin.Id, entry.Extension, slot, contributionId, context, token), null, cancellationToken).ConfigureAwait(false);
                if (result.Error is { } error)
                {
                    LogRenderFailed(_logger, plugin.Id.Value, contributionId, error.GetType().Name);
                }
            }
        }
    }

    private PluginRuntimeStatus Failure(PluginId id, PluginInfo? info, string error)
    {
        LogLoadFailed(_logger, id.Value, error);
        return new PluginRuntimeStatus(id, info, PluginRuntimeState.Failed, error);
    }

    // The plugin's constructor and InitializeAsync run on a pool thread under a budget of 20 callbacks; failing here is not a fault, it is a failed load.
    private async Task<string?> InitializeAsync(LoadedPlugin plugin, PluginManifest manifest, byte[] image, PluginHostContext context, CancellationToken cancellationToken)
    {
        try
        {
            Assembly assembly = plugin.LoadContext.LoadPlugin(image);
            if (PluginInstallPipeline.EntryTypeProblem(assembly, manifest.EntryType) is { } problem)
            {
                return problem;
            }

            Type entryType = assembly.GetType(manifest.EntryType, throwOnError: true)!;
            var initializer = new PluginCircuitBreaker(plugin.Id, _clock, _options.Breaker with { Budget = _options.InitializeBudget });
            BreakerResult<bool> result = await initializer.ExecuteAsync(
                token => new ValueTask(Task.Run(
                    async () =>
                    {
                        var instance = (IBlockerPlugin)Activator.CreateInstance(entryType)!;
                        plugin.Instance = instance;
                        await instance.InitializeAsync(context, token).ConfigureAwait(false);
                    },
                    token)),
                cancellationToken).ConfigureAwait(false);
            return result.Outcome switch
            {
                BreakerOutcome.Success => null,
                BreakerOutcome.Timeout => $"Initialization did not finish within {_options.InitializeBudget.TotalMilliseconds:0} ms.",
                _ => $"Initialization failed: {Describe(result.Error)}",
            };
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or ReflectionTypeLoadException)
        {
            return $"The plugin could not be loaded: {PluginText.Printable(ex.Message, 300)}";
        }
    }

    private static string Describe(Exception? error)
    {
        Exception? cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        return cause is null ? "unknown error" : $"{cause.GetType().Name}: {PluginText.Printable(cause.Message, 300)}";
    }

    private void OnPluginFaulted(LoadedPlugin plugin, string reason)
    {
        _bus.RemovePlugin(plugin.Id);
        LogFaulted(_logger, plugin.Id.Value, reason);
        try
        {
            PluginFaulted?.Invoke(this, new PluginFaultedEventArgs(plugin.Id, reason));
        }
        catch (Exception ex)
        {
            // This runs inside the circuit breaker: a failing subscriber must not turn into a failure of the guarded call.
            LogSubscriberFailed(_logger, ex, nameof(PluginFaulted));
        }
    }

    private void RaiseRenderRequested(PluginId pluginId, string contributionId)
    {
        try
        {
            RenderRequested?.Invoke(this, new RenderRequestedEventArgs(pluginId, contributionId));
        }
        catch (Exception ex)
        {
            LogSubscriberFailed(_logger, ex, nameof(RenderRequested));
        }
    }

    private async Task UnloadAsync(LoadedPlugin plugin, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _plugins.Remove(plugin.Id);
        }

        _bus.RemovePlugin(plugin.Id);
        await StopInstanceAsync(plugin, cancellationToken).ConfigureAwait(false);
        await DiscardAsync(plugin).ConfigureAwait(false);
    }

    private async Task StopInstanceAsync(LoadedPlugin plugin, CancellationToken cancellationToken)
    {
        if (plugin.Instance is not { } instance)
        {
            return;
        }

        using var cap = new CancellationTokenSource(_options.ShutdownTimeout, _clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cap.Token, cancellationToken);
        Task shutdown = Task.Run(() => instance.ShutdownAsync(linked.Token).AsTask(), CancellationToken.None);
        try
        {
            await shutdown.WaitAsync(_options.ShutdownTimeout, _clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogShutdownTimedOut(_logger, plugin.Id.Value);
            await linked.CancelAsync().ConfigureAwait(false);
            _ = shutdown.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogShutdownFailed(_logger, ex, plugin.Id.Value);
        }
    }

    private async Task DiscardAsync(LoadedPlugin plugin)
    {
        _bus.RemovePlugin(plugin.Id);
        await plugin.CloseAsync().ConfigureAwait(false);
        plugin.LoadContext.Unload();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin {PluginId} loaded.")]
    private static partial void LogLoaded(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} failed to load: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string pluginId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} was faulted: {Reason}")]
    private static partial void LogFaulted(ILogger logger, string pluginId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rendering {ContributionId} of plugin {PluginId} failed ({Reason}).")]
    private static partial void LogRenderFailed(ILogger logger, string pluginId, string contributionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId}: slot {Slot} exceeds the response budget; contribution skipped.")]
    private static partial void LogSlotBudget(ILogger logger, string pluginId, UiSlot slot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} did not shut down in time.")]
    private static partial void LogShutdownTimedOut(ILogger logger, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin {PluginId} failed during shutdown.")]
    private static partial void LogShutdownFailed(ILogger logger, Exception exception, string pluginId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscriber of {Event} failed.")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception, string @event);

    private sealed class ContributionSurface(PluginHostRuntime runtime, PluginId pluginId, string contributionId) : IUiSurface
    {
        public void RequestRender() => runtime.RaiseRenderRequested(pluginId, contributionId);
    }
}
