// Focus Forest: every finished detox session grows a small isometric forest.
//
// The plugin keeps its own session tracking (plugins are isolated and share no stores):
//   * events      SessionStarted/SessionEnded (events.session) drive coins and plantings
//   * storage     one compact document under the key "forest" (see ForestStore), the running session included
//   * UI          a page (ui.page), a small dashboard widget (ui.dashboard) and a settings section (ui.settings)
//   * 50 ms rule  renders read the in-memory snapshot only; storage is never read while rendering

using System.Globalization;
using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.UI.Sdk;

namespace FocusForest;

/// <summary>Entry point named in plugin.json.</summary>
public sealed class ForestPlugin : IBlockerPlugin
{
    /// <inheritdoc/>
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var store = new ForestStore(context.Storage, context.Time, context.Logger);
        await store.LoadAsync(cancellationToken);

        context.Events.Subscribe<SessionStarted>((e, ct) => store.OnSessionStartedAsync(e, ct));
        context.Events.Subscribe<SessionEnded>((e, ct) => store.OnSessionEndedAsync(e, ct));

        context.Extensions.Add(new ForestPage(store));
        context.Extensions.Add(new ForestWidget(store));
        context.Extensions.Add(new ForestSettings(store));
    }

    /// <inheritdoc/>
    public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>The plugin's own logo: a stepped pine on a trunk inside a closed focus ring (0..100 box, even-odd safe, the tree does not touch the ring).</summary>
public static class ForestLogo
{
    /// <summary>Path data using only M, A, L, H, V and Z.</summary>
    public const string PathData =
        "M50 4 A46 46 0 1 1 50 96 A46 46 0 1 1 50 4 Z M50 12 A38 38 0 1 0 50 88 A38 38 0 1 0 50 12 Z " +
        "M50 24 L63 42 H57 L70 62 H30 L43 42 H37 Z M45 70 H55 V80 H45 Z";
}

/// <summary>Remembers the surface of a contribution and re-renders it whenever the forest changes.</summary>
public abstract class ForestContribution : IUiContribution
{
    private IUiSurface? _surface;

    /// <summary>Subscribes to store changes.</summary>
    protected ForestContribution(ForestStore store)
    {
        Store = store;
        store.Changed += () => Volatile.Read(ref _surface)?.RequestRender();
    }

    /// <summary>The shared, cached state.</summary>
    protected ForestStore Store { get; }

    /// <inheritdoc/>
    public virtual ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>Call at the start of every render and action so a later render can be requested.</summary>
    protected void Remember(UiRenderContext context) => Volatile.Write(ref _surface, context.Surface);
}

/// <summary>The Forest page: scene, stats, inventory and shop. Requires "ui.page".</summary>
public sealed class ForestPage(ForestStore store) : ForestContribution(store), IUiPagePlugin
{
    private int _shopCategory;

    /// <inheritdoc/>
    public UiNavigationItem Navigation { get; } = new("Forest", UiIcon.Leaf);

    /// <inheritdoc/>
    public ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        List<UiNode> body = ForestLayout.Body(Store.GetView(), Store.Time.LocalTimeZone, interactive: true, (ShopCategory)Volatile.Read(ref _shopCategory));
        return ValueTask.FromResult(new UiPage("Forest", "Every focus session grows your forest.", body));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind == UiActionKind.Pick && action.ElementId == ForestIds.Scene
            && action.FormValues.TryGetValue("column", out string? column) && int.TryParse(column, NumberStyles.None, CultureInfo.InvariantCulture, out int c)
            && action.FormValues.TryGetValue("row", out string? row) && int.TryParse(row, NumberStyles.None, CultureInfo.InvariantCulture, out int r))
        {
            await Store.PickAsync(c, r, cancellationToken);
            return;
        }

        if (action.Kind != UiActionKind.Click)
        {
            return;
        }

        string id = action.ElementId;
        if (id.StartsWith(ForestIds.ShopTabPrefix, StringComparison.Ordinal))
        {
            foreach (ShopCategory category in Enum.GetValues<ShopCategory>())
            {
                if (id == ForestIds.ShopTab(category))
                {
                    Volatile.Write(ref _shopCategory, (int)category);
                    context.Surface.RequestRender();
                }
            }
        }
        else if (id == ForestIds.Cancel)
        {
            Store.CancelSelection();
        }
        else if (id == ForestIds.Remove)
        {
            if (Store.GetView().SelectedObject is { } selected)
            {
                await Store.RemoveWitheredAsync(selected, cancellationToken);
            }
        }
        else if (id.StartsWith(ForestIds.ObjectPrefix, StringComparison.Ordinal))
        {
            if (int.TryParse(id.AsSpan(ForestIds.ObjectPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int objectId))
            {
                Store.ToggleSelect(objectId);
            }
        }
        else if (id.StartsWith(ForestIds.UnlockPrefix, StringComparison.Ordinal))
        {
            await Store.UnlockAsync(id[ForestIds.UnlockPrefix.Length..], cancellationToken);
        }
        else if (id.StartsWith(ForestIds.SelectPrefix, StringComparison.Ordinal))
        {
            await Store.SelectSpeciesAsync(id[ForestIds.SelectPrefix.Length..], cancellationToken);
        }
        else if (id.StartsWith(ForestIds.BuyPrefix, StringComparison.Ordinal))
        {
            await Store.BuyDecorationAsync(id[ForestIds.BuyPrefix.Length..], cancellationToken);
        }
        else if (id.StartsWith(ForestIds.PlacePrefix, StringComparison.Ordinal))
        {
            Store.TogglePlacing(id[ForestIds.PlacePrefix.Length..]);
        }
    }
}

/// <summary>Small dashboard widget: coins and plants. The host draws the surface, icon, name and tap-to-open. Requires "ui.dashboard".</summary>
public sealed class ForestWidget(ForestStore store) : ForestContribution(store), IDashboardWidgetPlugin
{
    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Small;

    /// <inheritdoc/>
    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        ForestView view = Store.GetView();
        int plants = view.Objects.Count(o => ForestCatalog.Find(o.Species)?.Kind != ForestKind.Decoration);
        List<UiNode> body = [Ui.Stat("Forest", ForestFormat.Count(plants, "plant"), ForestFormat.Count(view.Coins, "coin"), UiTone.Success)];
        if (plants == 0)
        {
            body.Add(Ui.Text("Finish a session to plant your first tree.", UiTextStyle.Caption));
        }

        return ValueTask.FromResult(Ui.Card([.. body]));
    }
}

/// <summary>Settings: resetting the forest. Requires "ui.settings".</summary>
public sealed class ForestSettings(ForestStore store) : ForestContribution(store), ISettingsSectionPlugin
{
    /// <summary>Id of the reset button.</summary>
    public const string ResetButtonId = "forest-reset";

    /// <summary>How long the reset button waits for its confirming second click.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private long _armedAt;
    private int _armed;

    /// <inheritdoc/>
    public string Title => "Focus Forest";

    /// <inheritdoc/>
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        ForestView view = Store.GetView();
        string stored = view.Objects.Count == 0 && view.Coins == 0
            ? "Your forest is empty."
            : $"Your forest has {ForestFormat.Count(view.Objects.Count, "object")} and {ForestFormat.Count(view.Coins, "coin")}.";
        UiButton reset = IsArmed()
            ? Ui.Button(ResetButtonId, "Reset forest? Click again", UiButtonVariant.Warning, UiIcon.Warning)
            : Ui.Button(ResetButtonId, "Reset forest", UiButtonVariant.Secondary, UiIcon.Dismiss);
        return ValueTask.FromResult<UiNode>(Ui.Column(
            Ui.Text(stored, UiTextStyle.Caption),
            reset));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind != UiActionKind.Click || action.ElementId != ResetButtonId)
        {
            return;
        }

        if (IsArmed())
        {
            Volatile.Write(ref _armed, 0);
            await Store.ResetAsync(cancellationToken);   // raises Changed, which requests a render
        }
        else
        {
            Volatile.Write(ref _armedAt, Store.Time.GetTimestamp());
            Volatile.Write(ref _armed, 1);
            context.Surface.RequestRender();
        }
    }

    private bool IsArmed() =>
        Volatile.Read(ref _armed) == 1 && Store.Time.GetElapsedTime(Volatile.Read(ref _armedAt)) < ConfirmWindow;
}

/// <summary>Small text helpers.</summary>
public static class ForestFormat
{
    /// <summary>"1 coin", "3 coins".</summary>
    public static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? string.Empty : "s")}");

    /// <summary>"Nov 5".</summary>
    public static string DayLabel(int dayNumber) => DateOnly.FromDayNumber(dayNumber).ToString("MMM d", CultureInfo.InvariantCulture);
}
