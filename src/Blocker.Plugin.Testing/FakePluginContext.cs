using System.Collections.Frozen;
using System.Text.Json;
using Blocker.Sdk;
using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Ipc;
using Blocker.Sdk.Session;
using Blocker.Sdk.Storage;
using Blocker.UI.Sdk;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Plugin.Testing;

/// <summary>
/// Test double of <see cref="IPluginContext"/> that enforces the same capability, naming and size rules as the
/// production host, runs everything inline on the calling thread and uses a controllable clock.
/// </summary>
public sealed class FakePluginContext : IPluginContext
{
    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeEventBus _events;
    private readonly FakeExtensionRegistry _extensions;
    private readonly FakeIpcRegistry _ipc;
    private readonly CountingSurface _surface = new();

    /// <summary>Creates a context for a plugin.</summary>
    /// <param name="pluginId">Plugin id; must satisfy <see cref="PluginId.IsValid"/>.</param>
    /// <param name="capabilities">Granted capabilities; when empty, every capability is granted.</param>
    public FakePluginContext(string pluginId = "com.example.test-plugin", params PluginCapability[] capabilities)
    {
        FakeTime = new FakeTimeProvider(StartTime);
        FakeSession = new FakeSessionInfo();
        FakeStorage = new InMemoryPluginStorage();
        FakeLogger = new RecordingPluginLogger();
        IReadOnlySet<PluginCapability> granted = (capabilities is { Length: > 0 } ? capabilities : Enum.GetValues<PluginCapability>())
            .ToFrozenSet();
        Plugin = new PluginInfo(new PluginId(pluginId), pluginId, new Version(1, 0, 0), "Test", granted);
        _events = new FakeEventBus(Plugin, FakeTime);
        _extensions = new FakeExtensionRegistry(Plugin, FakeTime);
        _ipc = new FakeIpcRegistry(Plugin);
    }

    /// <summary>Gets the controllable clock; starts at 2026-01-01T09:00:00Z.</summary>
    public FakeTimeProvider FakeTime { get; }

    /// <summary>Gets the session state; set <see cref="FakeSessionInfo.Current"/> to simulate a session.</summary>
    public FakeSessionInfo FakeSession { get; }

    /// <summary>Gets the in-memory storage (1 MiB quota).</summary>
    public InMemoryPluginStorage FakeStorage { get; }

    /// <summary>Gets the logger that records every entry.</summary>
    public RecordingPluginLogger FakeLogger { get; }

    /// <summary>Gets the number of <see cref="IUiSurface.RequestRender"/> calls made by contributions.</summary>
    public int RenderRequests => _surface.Requests;

    /// <summary>Gets the extensions currently registered, in registration order.</summary>
    public IReadOnlyList<IPluginExtension> RegisteredExtensions => _extensions.Extensions;

    /// <summary>Gets the names of the registered IPC methods in ordinal order.</summary>
    public IReadOnlyCollection<string> RegisteredIpcMethods => _ipc.Methods;

    /// <inheritdoc/>
    public PluginInfo Plugin { get; }

    /// <inheritdoc/>
    public IEventBus Events => _events;

    /// <inheritdoc/>
    public IExtensionRegistry Extensions => _extensions;

    /// <inheritdoc/>
    public IIpcRegistry Ipc => _ipc;

    /// <inheritdoc/>
    public IPluginStorage Storage => FakeStorage;

    /// <inheritdoc/>
    public ISessionInfo Session => FakeSession;

    /// <inheritdoc/>
    public TimeProvider Time => FakeTime;

    /// <inheritdoc/>
    public IPluginLogger Logger => FakeLogger;

    /// <summary>Returns the single registered extension assignable to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Extension type.</typeparam>
    /// <returns>The extension.</returns>
    /// <exception cref="InvalidOperationException">No extension, or more than one, matches.</exception>
    public T GetExtension<T>()
        where T : class
    {
        T[] matches = [.. RegisteredExtensions.OfType<T>()];
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected exactly one registered extension of type '{typeof(T).Name}' but found {matches.Length}.");
    }

    /// <summary>Test-only: delivers a core event to its subscribers, sequentially and in subscription order.</summary>
    /// <typeparam name="TEvent">Event type.</typeparam>
    /// <param name="e">The event; the capability check is bypassed because only the host publishes core events.</param>
    /// <param name="cancellationToken">Cancels delivery between handlers.</param>
    /// <returns>A task that completes after the last handler.</returns>
    public ValueTask PublishAsync<TEvent>(TEvent e, CancellationToken cancellationToken = default)
        where TEvent : class, IBlockerEvent =>
        _events.DispatchAsync(e, cancellationToken);

    /// <summary>Calls a registered IPC method as an external client would.</summary>
    /// <param name="method">Method name as registered by the plugin.</param>
    /// <param name="payloadJson">Request JSON.</param>
    /// <param name="cancellationToken">Passed to the handler.</param>
    /// <returns>The response JSON.</returns>
    /// <exception cref="InvalidOperationException">The method is not registered or returned an invalid response.</exception>
    /// <exception cref="ArgumentException">The payload is not valid JSON or exceeds <see cref="BlockerSdk.MaxPayloadBytes"/>.</exception>
    public ValueTask<string> InvokeIpcAsync(string method, string payloadJson, CancellationToken cancellationToken = default) =>
        _ipc.InvokeAsync(method, payloadJson, cancellationToken);

    /// <summary>
    /// Advances <see cref="FakeTime"/> and invokes every registered <see cref="ITickListener"/> once per elapsed
    /// interval (at least one second), in chronological order. The clock reads the due time during each tick.
    /// </summary>
    /// <param name="advance">Non-negative amount of time to advance.</param>
    /// <param name="cancellationToken">Cancels between ticks and is passed to listeners.</param>
    /// <returns>A task that completes after the last tick.</returns>
    public async ValueTask TickAsync(TimeSpan advance, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(advance, TimeSpan.Zero);
        DateTimeOffset target = FakeTime.GetUtcNow() + advance;
        while (_extensions.TakeNextDue(target) is var (listener, due))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FakeTime.Advance(due - FakeTime.GetUtcNow());
            await listener.OnTickAsync(new TickContext(due, FakeSession.Current), cancellationToken).ConfigureAwait(false);
        }

        FakeTime.Advance(target - FakeTime.GetUtcNow());
    }

    /// <summary>
    /// Renders a contribution and validates the tree as the host does: <see cref="UiTreeValidator"/> (plus
    /// <see cref="UiTreeValidator.ValidateNavigation"/> for pages) and a <see cref="UiJson"/> round trip.
    /// A contribution implementing several UI interfaces is rendered as the first of page, dashboard widget,
    /// settings section, block screen.
    /// </summary>
    /// <param name="contribution">Contribution to render.</param>
    /// <param name="cancellationToken">Passed to the contribution.</param>
    /// <returns>The tree after the JSON round trip, exactly as the UI process would receive it.</returns>
    /// <exception cref="UiValidationException">The tree or navigation entry is invalid or not serializable.</exception>
    /// <exception cref="ArgumentException">The contribution implements no UI interface.</exception>
    public async ValueTask<UiNode> RenderAsync(IUiContribution contribution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var context = new UiRenderContext(FakeSession.Current, _surface);
        List<string> errors = [];
        UiNode root;
        switch (contribution)
        {
            case IUiPagePlugin page:
                errors.AddRange(UiTreeValidator.ValidateNavigation(page.Navigation).Errors);
                root = await page.RenderAsync(context, cancellationToken).ConfigureAwait(false);
                break;
            case IDashboardWidgetPlugin widget:
                root = await widget.RenderAsync(context, cancellationToken).ConfigureAwait(false);
                break;
            case ISettingsSectionPlugin section:
                root = await section.RenderAsync(context, cancellationToken).ConfigureAwait(false);
                break;
            case IBlockScreenPlugin blockScreen:
                root = await blockScreen.RenderAsync(context, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"'{contribution.GetType().Name}' implements no UI contribution interface.", nameof(contribution));
        }

        if (contribution is not IUiPagePlugin && root is UiPage)
        {
            errors.Add("UiPage is only allowed for IUiPagePlugin.");
        }

        errors.AddRange(UiTreeValidator.Validate(root).Errors);
        if (errors.Count > 0)
        {
            throw new UiValidationException(errors);
        }

        UiNode roundTripped = RoundTrip(root);
        errors.AddRange(UiTreeValidator.Validate(roundTripped).Errors);
        return errors.Count > 0 ? throw new UiValidationException(errors) : roundTripped;
    }

    /// <summary>Delivers a user interaction to a contribution.</summary>
    /// <param name="contribution">Target contribution.</param>
    /// <param name="action">The interaction.</param>
    /// <param name="cancellationToken">Passed to the contribution.</param>
    /// <returns>A task that completes when the action is handled.</returns>
    public ValueTask SendActionAsync(IUiContribution contribution, UiAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(action);
        return contribution.OnActionAsync(action, new UiRenderContext(FakeSession.Current, _surface), cancellationToken);
    }

    private static UiNode RoundTrip(UiNode root)
    {
        try
        {
            return UiJson.Deserialize(UiJson.Serialize(root));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new UiValidationException([$"The UI tree does not survive a JSON round trip: {ex.Message}"]);
        }
    }
}
