// Focus Counter: a small, complete example of a Blocker plugin.
//
// What it shows:
//   * lifecycle      IBlockerPlugin.InitializeAsync / ShutdownAsync
//   * capabilities   every API used here has a matching entry in plugin.json "capabilities"
//   * events         SessionStarted (events.session) and ProcessBlocked (events.process)
//   * storage        private key/value storage that survives restarts
//   * UI             dashboard widget, settings section and block-screen panel, all declarative
//   * 50 ms rule     RenderAsync never does I/O: it renders from state cached in memory
//   * RequestRender  ask the host to render again after the state changed
//
// Rules in short (full list: AGENT_INSTRUCTIONS.md): use context.Time instead of DateTime.Now, never use
// Process/File/Registry/P-Invoke/reflection, no async void, validate every action you receive.

using System.Globalization;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.Sdk.Storage;
using Blocker.UI.Sdk;

namespace FocusCounter;

/// <summary>Entry point named in plugin.json ("entryType"). Needs a public parameterless constructor.</summary>
public sealed class PluginMain : IBlockerPlugin
{
    private FocusCounterState? _state;

    /// <summary>Called once after loading. Subscribe to events and register extensions here.</summary>
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        // Load persisted values ONCE into memory; later callbacks only read the cached copy.
        var state = new FocusCounterState(context.Storage);
        await state.LoadAsync(cancellationToken);
        _state = state;

        // Each Subscribe call is checked against the manifest: a missing capability throws
        // PluginCapabilityException, so plugin.json must list "events.session" and "events.process".
        // Registrations are removed automatically when the plugin unloads.
        context.Events.Subscribe<SessionStarted>((evt, ct) => state.StartSessionAsync(evt.PlannedDuration, ct));
        context.Events.Subscribe<ProcessBlocked>((_, ct) =>
            context.Session.Current.IsSessionRunning ? state.IncrementAsync(ct) : ValueTask.CompletedTask);

        // Extensions need "ui.dashboard", "ui.settings" and "ui.blockscreen" respectively.
        context.Extensions.Add(new FocusWidget(state));
        context.Extensions.Add(new FocusSettings(state));
        context.Extensions.Add(new FocusBlockScreen(state));
    }

    /// <summary>Called once before unloading. Flush anything that is not saved yet.</summary>
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_state is { } state)
        {
            await state.FlushAsync(cancellationToken);
        }
    }
}

/// <summary>
/// In-memory cache of the plugin state, written through to storage on every change. Callbacks are free to run
/// concurrently, so fields are only touched through <see cref="Interlocked"/>/<see cref="Volatile"/>.
/// </summary>
public sealed class FocusCounterState
{
    private const string CountKey = "count";                    // storage keys: a-z 0-9 . _ - (max 128)
    private const string ShowOnBlockScreenKey = "show-on-block-screen";

    private readonly IPluginStorage _storage;
    private int _count;
    private int _showOnBlockScreen = 1;                         // default: enabled
    private long _plannedTicks;                                 // planned length of the running session

    /// <summary>Creates the state on top of the plugin's private storage.</summary>
    public FocusCounterState(IPluginStorage storage) => _storage = storage;

    /// <summary>Raised after any change so contributions can ask the host to render again.</summary>
    public event Action? Changed;

    /// <summary>Distractions resisted since the current (or last) session started.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Whether the counter is displayed on the block screen.</summary>
    public bool ShowOnBlockScreen => Volatile.Read(ref _showOnBlockScreen) == 1;

    /// <summary>Planned length of the running session, or null when it is unknown (plugin loaded mid-session).</summary>
    public TimeSpan? PlannedDuration => Volatile.Read(ref _plannedTicks) is > 0 and var ticks ? new TimeSpan(ticks) : null;

    /// <summary>Reads persisted values. Missing or corrupt values fall back to the defaults.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        string? count = await _storage.ReadAsync(CountKey, cancellationToken);
        if (int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            Volatile.Write(ref _count, parsed);
        }

        string? show = await _storage.ReadAsync(ShowOnBlockScreenKey, cancellationToken);
        if (bool.TryParse(show, out bool enabled))
        {
            Volatile.Write(ref _showOnBlockScreen, enabled ? 1 : 0);
        }
    }

    /// <summary>Writes every value to storage.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _storage.WriteAsync(CountKey, Count.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _storage.WriteAsync(ShowOnBlockScreenKey, ShowOnBlockScreen ? "true" : "false", cancellationToken);
    }

    /// <summary>Resets the counter for a new session.</summary>
    public async ValueTask StartSessionAsync(TimeSpan plannedDuration, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _plannedTicks, plannedDuration.Ticks);
        await ResetAsync(cancellationToken);
    }

    /// <summary>Counts one more resisted distraction.</summary>
    public async ValueTask IncrementAsync(CancellationToken cancellationToken)
    {
        int count = Interlocked.Increment(ref _count);
        await _storage.WriteAsync(CountKey, count.ToString(CultureInfo.InvariantCulture), cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Sets the counter back to zero.</summary>
    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _count, 0);
        await _storage.WriteAsync(CountKey, "0", cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Changes and persists the "show on block screen" setting.</summary>
    public async ValueTask SetShowOnBlockScreenAsync(bool value, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _showOnBlockScreen, value ? 1 : 0);
        await _storage.WriteAsync(ShowOnBlockScreenKey, value ? "true" : "false", cancellationToken);
        Changed?.Invoke();
    }
}

/// <summary>Remembers the surface of a contribution and re-renders it whenever the shared state changes.</summary>
public abstract class FocusContribution : IUiContribution
{
    private IUiSurface? _surface;

    /// <summary>Subscribes to state changes.</summary>
    protected FocusContribution(FocusCounterState state)
    {
        State = state;
        state.Changed += () => Volatile.Read(ref _surface)?.RequestRender();
    }

    /// <summary>The shared, cached plugin state.</summary>
    protected FocusCounterState State { get; }

    /// <summary>Handles a user interaction. Default: ignore. Always validate what you receive.</summary>
    public virtual ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>Call at the start of every render/action so <see cref="IUiSurface.RequestRender"/> can be used later.</summary>
    protected void Remember(UiRenderContext context) => Volatile.Write(ref _surface, context.Surface);
}

/// <summary>Dashboard card: counter, remaining session time and a reset button. Requires "ui.dashboard".</summary>
public sealed class FocusWidget(FocusCounterState state) : FocusContribution(state), IDashboardWidgetPlugin
{
    /// <summary>Id of the reset button.</summary>
    public const string ResetButtonId = "reset";

    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Medium;

    /// <inheritdoc/>
    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        // 50 ms rule: no I/O here, only cached state and the snapshot in the render context.
        Remember(context);
        bool running = context.Session.IsSessionRunning;
        List<UiNode> body =
        [
            Ui.Stat(
                "Distractions resisted",
                State.Count.ToString(CultureInfo.InvariantCulture),
                running ? "Since the session started" : "In the last session",
                UiTone.Success,
                UiIcon.Shield),
        ];

        if (running && context.Session.Remaining is { } remaining)
        {
            double left = Math.Max(0, remaining.TotalSeconds);
            double total = Math.Max(Math.Max(State.PlannedDuration?.TotalSeconds ?? left, left), 1);
            body.Add(Ui.Progress(left, total, $"Session time left: {FormatDuration(remaining)}"));
        }

        body.Add(Ui.Button(ResetButtonId, "Reset counter", UiButtonVariant.Subtle, UiIcon.Dismiss));
        return ValueTask.FromResult(Ui.Card("Focus Counter", [.. body]));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind == UiActionKind.Click && action.ElementId == ResetButtonId)
        {
            await State.ResetAsync(cancellationToken);   // raises Changed, which calls RequestRender
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        int minutes = (int)Math.Max(0, duration.TotalMinutes);
        return minutes >= 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:00} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
    }
}

/// <summary>Settings section with one persisted toggle. Requires "ui.settings".</summary>
public sealed class FocusSettings(FocusCounterState state) : FocusContribution(state), ISettingsSectionPlugin
{
    /// <summary>Id of the toggle.</summary>
    public const string ToggleId = "show-on-block-screen";

    /// <inheritdoc/>
    public string Title => "Focus Counter";

    /// <inheritdoc/>
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.FromResult<UiNode>(Ui.Column(
            Ui.Toggle(ToggleId, "Show on block screen", State.ShowOnBlockScreen, "Display how many distractions you resisted when an app is blocked.")));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        // Validate: right element, right kind, and a value that is exactly "true" or "false".
        if (action.Kind == UiActionKind.Toggle && action.ElementId == ToggleId && bool.TryParse(action.Value, out bool isOn))
        {
            await State.SetShowOnBlockScreenAsync(isOn, cancellationToken);
        }
    }
}

/// <summary>Panel on the block screen. Requires "ui.blockscreen". Cannot end the session or bypass enforcement.</summary>
public sealed class FocusBlockScreen(FocusCounterState state) : FocusContribution(state), IBlockScreenPlugin
{
    /// <inheritdoc/>
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);

        // An empty stack renders nothing: this is how a contribution hides itself.
        if (!State.ShowOnBlockScreen)
        {
            return ValueTask.FromResult<UiNode>(Ui.Column());
        }

        int count = State.Count;
        string noun = count == 1 ? "distraction" : "distractions";
        return ValueTask.FromResult<UiNode>(
            Ui.Text(string.Create(CultureInfo.InvariantCulture, $"You resisted {count} {noun}"), UiTextStyle.Subtitle, UiTone.Success));
    }
}
