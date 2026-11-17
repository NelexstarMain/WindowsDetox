using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using Blocker.UI.Sdk;

namespace Blocker.SDK.Tests;

public class ExtensionCapabilitiesTests
{
    [Fact]
    public void GetRequired_MarkerOnly_ReturnsEmpty() =>
        Assert.Empty(ExtensionCapabilities.GetRequired(new MarkerOnly()));

    [Fact]
    public void GetRequired_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => ExtensionCapabilities.GetRequired(null!));

    [Fact]
    public void GetRequired_SingleInterface_ReturnsItsCapability()
    {
        Assert.Equal([PluginCapability.ProcessInterception], ExtensionCapabilities.GetRequired(new ProcessOnly()));
        Assert.Equal([PluginCapability.Tick], ExtensionCapabilities.GetRequired(new TickOnly()));
        Assert.Equal([PluginCapability.UiBlockScreen], ExtensionCapabilities.GetRequired(new BlockScreenOnly()));
    }

    [Fact]
    public void GetRequired_MultipleInterfaces_ReturnsAllInDeclaredOrder()
    {
        Assert.Equal(
            [PluginCapability.ProcessInterception, PluginCapability.WindowInterception, PluginCapability.Tick, PluginCapability.LockPolicy],
            ExtensionCapabilities.GetRequired(new BackendMulti()));
        Assert.Equal(
            [PluginCapability.UiPage, PluginCapability.UiDashboardWidget],
            ExtensionCapabilities.GetRequired(new UiMulti()));
    }

    private sealed class MarkerOnly : IPluginExtension;

    private sealed class ProcessOnly : IProcessInterceptor
    {
        public ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
            ValueTask.FromResult(InterceptDecision.Abstain);
    }

    private sealed class TickOnly : ITickListener
    {
        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class BlockScreenOnly : IBlockScreenPlugin
    {
        public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<UiNode>(Ui.Text("x"));
    }

    private sealed class BackendMulti : IProcessInterceptor, IWindowInterceptor, ITickListener, ILockPolicyContributor
    {
        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
            ValueTask.FromResult(InterceptDecision.Abstain);

        public ValueTask<InterceptDecision> OnWindowActivatedAsync(WindowDescriptor window, CancellationToken cancellationToken) =>
            ValueTask.FromResult(InterceptDecision.Abstain);

        public ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<LockVote> EvaluateAsync(LockAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult(LockVote.Allow);
    }

    private sealed class UiMulti : IUiPagePlugin, IDashboardWidgetPlugin
    {
        public UiNavigationItem Navigation => new("Page", UiIcon.Clock);

        public UiWidgetSize Size => UiWidgetSize.Small;

        ValueTask<UiPage> IUiPagePlugin.RenderAsync(UiRenderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Ui.Page("T"));

        ValueTask<UiCard> IDashboardWidgetPlugin.RenderAsync(UiRenderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Ui.Card("T"));
    }
}
