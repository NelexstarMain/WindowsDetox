using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.UI.Sdk;

namespace Blocker.Plugin.Testing.Tests;

/// <summary>Records the clock reading and session of every tick.</summary>
internal sealed class RecordingTickListener(string name, TimeSpan interval, List<string> log) : ITickListener
{
    public List<TickContext> Contexts { get; } = [];

    public TimeSpan Interval { get; } = interval;

    public ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken)
    {
        Contexts.Add(context);
        log.Add($"{name}@{context.Now:HH:mm:ss}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Implements two extension interfaces so the first missing capability can be observed.</summary>
internal sealed class InterceptingTicker : ITickListener, IProcessInterceptor
{
    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
        ValueTask.FromResult(InterceptDecision.Abstain);
}

/// <summary>An extension that implements no known interface.</summary>
internal sealed class UselessExtension : IPluginExtension;

/// <summary>A UI contribution without a render interface.</summary>
internal sealed class BareContribution : IUiContribution;

/// <summary>Page whose tree and navigation are supplied by the test; records received actions.</summary>
internal sealed class SamplePage(UiNavigationItem navigation, UiPage page) : IUiPagePlugin
{
    public List<UiAction> Actions { get; } = [];

    public UiNavigationItem Navigation { get; } = navigation;

    public ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken) => ValueTask.FromResult(page);

    public ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Actions.Add(action);
        context.Surface.RequestRender();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Widget showing the remaining session time.</summary>
internal sealed class SampleWidget : IDashboardWidgetPlugin
{
    public UiWidgetSize Size => UiWidgetSize.Small;

    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Ui.Card("Time left", Ui.Text($"{context.Session.Remaining?.TotalMinutes:0} min")));
}

/// <summary>Settings section returning the node supplied by the test.</summary>
internal sealed class SampleSettings(UiNode node) : ISettingsSectionPlugin
{
    public string Title => "Sample";

    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken) => ValueTask.FromResult(node);
}

/// <summary>Block-screen element returning the node supplied by the test.</summary>
internal sealed class SampleBlockScreen(UiNode node) : IBlockScreenPlugin
{
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken) => ValueTask.FromResult(node);
}
