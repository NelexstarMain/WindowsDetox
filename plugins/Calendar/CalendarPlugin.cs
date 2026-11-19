// Calendar: an own, fully local calendar with planned focus sessions and tasks.
//
//   * storage     "data" (entries), "history" (what happened to planned sessions) and "settings" (see CalendarStore); nothing leaves the PC
//   * UI          a page (ui.page), a compact and a rich dashboard widget (ui.dashboard), a settings section (ui.settings)
//   * auto-start  a tick listener (tick) starts a planned session through ISessionLauncher (session.start) when its time arrives,
//                 unless a session already runs, and shows a reminder first through IPluginNotifier (notify)
//   * 50 ms rule  every render reads the in-memory copy; the auto-start decision is in-memory too, the service call runs in the background

using Blocker.Sdk;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>Entry point named in plugin.json.</summary>
public sealed class CalendarPlugin : IBlockerPlugin
{
    private CalendarStore? _store;
    private CalendarAutoStarter? _starter;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var store = new CalendarStore(context.Storage, context.Time, context.Logger);
        await store.LoadAsync(cancellationToken);
        _store = store;
        var state = new CalendarUiState(context.Time);
        var controller = new CalendarController(store, state);
        var starter = new CalendarAutoStarter(store, context.Sessions, context.Notifier, context.Logger);
        _starter = starter;

        context.Extensions.Add(new CalendarPage(store, state, controller));
        context.Extensions.Add(new CalendarWidget(store, state, controller));
        context.Extensions.Add(new CalendarCompactWidget(store, state, controller));
        context.Extensions.Add(new CalendarSettingsSection(store, state, controller));
        context.Extensions.Add(starter);
    }

    /// <inheritdoc/>
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_starter is { } starter)
        {
            await starter.StopAsync();
        }

        if (_store is { } store)
        {
            await store.FlushAsync(cancellationToken);
        }
    }
}

/// <summary>Remembers the surface of a contribution and re-renders it whenever the data or the UI state changes.</summary>
public abstract class CalendarContribution : IUiContribution
{
    private IUiSurface? _surface;

    /// <summary>Subscribes to changes.</summary>
    protected CalendarContribution(CalendarStore store, CalendarUiState state, CalendarController controller)
    {
        Store = store;
        State = state;
        Controller = controller;
        store.Changed += RequestRender;
        state.Changed += RequestRender;
    }

    /// <summary>The cached data.</summary>
    protected CalendarStore Store { get; }

    /// <summary>The UI state.</summary>
    protected CalendarUiState State { get; }

    /// <summary>The action handler.</summary>
    protected CalendarController Controller { get; }

    /// <inheritdoc/>
    public abstract ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken);

    /// <summary>Call at the start of every render and action so a later render can be requested.</summary>
    protected void Remember(UiRenderContext context) => Volatile.Write(ref _surface, context.Surface);

    private void RequestRender() => Volatile.Read(ref _surface)?.RequestRender();
}
