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

/// <summary>The plugin's own logo: a stepped pine on a trunk inside the open focus ring shared by the other plugins (0..100 box, even-odd safe, the tree does not touch the ring).</summary>
public static class ForestLogo
{
    /// <summary>Path data using only M, A, L, H, V and Z.</summary>
    public const string PathData =
        "M87.68 23.62 A46 46 0 1 1 54.01 4.17 L53.31 12.14 A38 38 0 1 0 81.13 28.2 Z " +
        "M50 20 L64 38 H58 L70 54 H62 L74 70 H55 V80 H45 V70 H26 L38 54 H30 L42 38 H36 Z";
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

/// <summary>The Forest page: scene, selection panel, planting choice, inventory and shop. Requires "ui.page".</summary>
public sealed class ForestPage(ForestStore store) : ForestContribution(store), IUiPagePlugin
{
    /// <summary>How long "Release all pins and arrange" waits for its confirming second click.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private int _shopCategory;
    private int _movingObject;   // id of the object waiting for a tile, 0 = none (ids start at 1)
    private long _releaseArmedAt;
    private int _releaseArmed;

    /// <inheritdoc/>
    public UiNavigationItem Navigation { get; } = new("Forest", UiIcon.Leaf);

    /// <inheritdoc/>
    public ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        ForestView view = Store.GetView();
        var state = new ForestUiState(view.SelectedObject is { } id && id == Volatile.Read(ref _movingObject), ReleaseArmed());
        List<UiNode> body = ForestLayout.Body(view, Store.Time.LocalTimeZone, interactive: true, (ShopCategory)Volatile.Read(ref _shopCategory), state);
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
            await PickTileAsync(c, r, cancellationToken);
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

            return;
        }

        if (id.StartsWith(ForestIds.PathPrefix, StringComparison.Ordinal))
        {
            string[] parts = id[ForestIds.PathPrefix.Length..].Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pathColumn)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pathRow))
            {
                await PickTileAsync(pathColumn, pathRow, cancellationToken);
            }

            return;
        }

        switch (id)
        {
            case ForestIds.Cancel:
                if (Interlocked.Exchange(ref _movingObject, 0) != 0 && Store.GetView().PlacingDecoration is null)
                {
                    context.Surface.RequestRender();   // back to the selected state
                }
                else
                {
                    Store.CancelSelection();
                }

                return;
            case ForestIds.Close:
                Volatile.Write(ref _movingObject, 0);
                Store.CancelSelection();
                return;
            case ForestIds.Move:
                if (Store.GetView().SelectedObject is { } toMove)
                {
                    Volatile.Write(ref _movingObject, toMove);
                    context.Surface.RequestRender();
                }

                return;
            case ForestIds.Pin:
                if (Store.GetView() is { SelectedObject: { } selectedId } view && view.Objects.FirstOrDefault(o => o.Id == selectedId) is { } target)
                {
                    await Store.SetPinnedAsync(selectedId.ToString(CultureInfo.InvariantCulture), !target.Pinned, cancellationToken);
                }

                return;
            case ForestIds.Remove:
                if (Store.GetView().SelectedObject is { } selected)
                {
                    // A decoration goes back to the inventory; anything else is a withered plant cleared for a fee.
                    if (!await Store.RemoveDecorationAsync(selected, cancellationToken))
                    {
                        await Store.RemoveWitheredAsync(selected, cancellationToken);
                    }
                }

                return;
            case ForestIds.Arrange:
                Volatile.Write(ref _releaseArmed, 0);
                await Store.ArrangeAsync(unpinAll: false, cancellationToken);
                context.Surface.RequestRender();
                return;
            case ForestIds.Release:
                if (ReleaseArmed())
                {
                    Volatile.Write(ref _releaseArmed, 0);
                    await Store.ArrangeAsync(unpinAll: true, cancellationToken);
                }
                else
                {
                    Volatile.Write(ref _releaseArmedAt, Store.Time.GetTimestamp());
                    Volatile.Write(ref _releaseArmed, 1);
                }

                context.Surface.RequestRender();
                return;
        }

        if (id.StartsWith(ForestIds.ObjectPrefix, StringComparison.Ordinal))
        {
            if (int.TryParse(id.AsSpan(ForestIds.ObjectPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int objectId))
            {
                Volatile.Write(ref _movingObject, 0);
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
        else if (id.StartsWith(ForestIds.ChipPrefix, StringComparison.Ordinal))
        {
            await Store.SelectSpeciesAsync(id[ForestIds.ChipPrefix.Length..], cancellationToken);
        }
        else if (id.StartsWith(ForestIds.BuyPrefix, StringComparison.Ordinal))
        {
            await Store.BuyDecorationAsync(id[ForestIds.BuyPrefix.Length..], cancellationToken);
        }
        else if (id.StartsWith(ForestIds.PlacePrefix, StringComparison.Ordinal))
        {
            Volatile.Write(ref _movingObject, 0);
            Store.TogglePlacing(id[ForestIds.PlacePrefix.Length..]);
        }
    }

    // A tile click only does something while an object is being moved or a decoration is being placed; the store ignores the rest.
    private async ValueTask PickTileAsync(int column, int row, CancellationToken cancellationToken)
    {
        ForestView view = Store.GetView();
        bool moving = view.SelectedObject is { } id && id == Volatile.Read(ref _movingObject);
        if (view.PlacingDecoration is null && !moving)
        {
            return;
        }

        if (await Store.PickAsync(column, row, cancellationToken))
        {
            Volatile.Write(ref _movingObject, 0);
        }
    }

    private bool ReleaseArmed() =>
        Volatile.Read(ref _releaseArmed) == 1 && Store.Time.GetElapsedTime(Volatile.Read(ref _releaseArmedAt)) < ConfirmWindow;
}

/// <summary>Dashboard widget: coins, plants and a mini forest. The host draws the surface, icon, name and tap-to-open. Requires "ui.dashboard".</summary>
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
        body.Add(plants == 0
            ? Ui.Text("Finish a session to plant your first tree.", UiTextStyle.Caption)
            : ForestLayout.MiniScene(view));
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
