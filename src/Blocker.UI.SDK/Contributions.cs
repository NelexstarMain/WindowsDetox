using Blocker.Sdk;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Session;

namespace Blocker.UI.Sdk;

/// <summary>A user interaction with an interactive element of a plugin's UI tree.</summary>
/// <param name="ElementId">Id of the button, toggle or form.</param>
/// <param name="Kind">Interaction kind.</param>
/// <param name="Value">Toggle state (<c>"true"</c>/<c>"false"</c>) for <see cref="UiActionKind.Toggle"/>; otherwise <see langword="null"/>.</param>
/// <param name="FormValues">Field id to value map for <see cref="UiActionKind.Submit"/>; otherwise empty. Validate like any untrusted input.</param>
public sealed record UiAction(string ElementId, UiActionKind Kind, string? Value, IReadOnlyDictionary<string, string> FormValues)
{
    /// <summary>Creates a click action.</summary>
    /// <param name="elementId">Button id.</param>
    /// <returns>The action.</returns>
    public static UiAction Click(string elementId) => new(elementId, UiActionKind.Click, null, EmptyValues);

    /// <summary>Creates a toggle action.</summary>
    /// <param name="elementId">Toggle id.</param>
    /// <param name="isOn">New state.</param>
    /// <returns>The action.</returns>
    public static UiAction Toggle(string elementId, bool isOn) =>
        new(elementId, UiActionKind.Toggle, isOn ? "true" : "false", EmptyValues);

    /// <summary>Creates a submit action.</summary>
    /// <param name="elementId">Form id.</param>
    /// <param name="values">Submitted field values.</param>
    /// <returns>The action.</returns>
    public static UiAction Submit(string elementId, IReadOnlyDictionary<string, string> values) =>
        new(elementId, UiActionKind.Submit, null, values);

    private static readonly IReadOnlyDictionary<string, string> EmptyValues = new Dictionary<string, string>();
}

/// <summary>Lets a contribution ask the host to render it again, for example after its data changed.</summary>
public interface IUiSurface
{
    /// <summary>Schedules a re-render. Calls are coalesced; at most one render per frame budget.</summary>
    void RequestRender();
}

/// <summary>Information passed to every render call.</summary>
/// <param name="Session">Current session snapshot.</param>
/// <param name="Surface">Surface of this contribution, used to request re-renders.</param>
public sealed record UiRenderContext(SessionSnapshot Session, IUiSurface Surface);

/// <summary>
/// Base of all UI contributions. Implementations are registered with <see cref="IExtensionRegistry.Add"/>.
/// Render and action callbacks share the <see cref="BlockerSdk.CallbackBudgetMilliseconds"/> budget; do slow
/// work elsewhere (for example in a tick listener) and render from cached state.
/// </summary>
public interface IUiContribution : IPluginExtension
{
    /// <summary>Handles a user interaction. The default implementation ignores it.</summary>
    /// <param name="action">The interaction.</param>
    /// <param name="context">Render context, used to request a re-render.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>A task that completes when the action is handled.</returns>
    ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Navigation entry of a plugin page.</summary>
/// <param name="Title">Menu text (at most 32 characters).</param>
/// <param name="Icon">Menu icon.</param>
public sealed record UiNavigationItem(string Title, UiIcon Icon);

/// <summary>Requires <see cref="PluginCapability.UiPage"/>. Adds a page to the side navigation.</summary>
public interface IUiPagePlugin : IUiContribution
{
    /// <summary>Gets the navigation entry.</summary>
    UiNavigationItem Navigation { get; }

    /// <summary>Renders the page.</summary>
    /// <param name="context">Render context.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The page tree.</returns>
    ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}

/// <summary>Requires <see cref="PluginCapability.UiDashboardWidget"/>. Adds a tile to the session dashboard.</summary>
public interface IDashboardWidgetPlugin : IUiContribution
{
    /// <summary>Gets the tile width.</summary>
    UiWidgetSize Size { get; }

    /// <summary>Renders the tile.</summary>
    /// <param name="context">Render context.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The tile, always a card.</returns>
    ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}

/// <summary>Requires <see cref="PluginCapability.UiSettingsSection"/>. Adds a section to the settings page.</summary>
public interface ISettingsSectionPlugin : IUiContribution
{
    /// <summary>Gets the section heading.</summary>
    string Title { get; }

    /// <summary>Renders the section body.</summary>
    /// <param name="context">Render context.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The section tree.</returns>
    ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Requires <see cref="PluginCapability.UiBlockScreen"/>. Adds content to the block screen shown by
/// <c>Blocker.Stub</c>. Block-screen content cannot end the session or bypass enforcement; it can only display
/// information and trigger the plugin's own actions.
/// </summary>
public interface IBlockScreenPlugin : IUiContribution
{
    /// <summary>Renders the block-screen element.</summary>
    /// <param name="context">Render context.</param>
    /// <param name="cancellationToken">Signalled when the callback budget is exhausted.</param>
    /// <returns>The element tree (no <see cref="UiPage"/>).</returns>
    ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}
