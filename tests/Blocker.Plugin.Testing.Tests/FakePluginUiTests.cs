using Blocker.Sdk.Diagnostics;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;

namespace Blocker.Plugin.Testing.Tests;

public sealed class FakePluginUiTests
{
    private static readonly UiNavigationItem Nav = new("Focus", UiIcon.Timer);

    [Fact]
    public async Task RenderAsync_Page_ReturnsRoundTrippedTree()
    {
        var context = new FakePluginContext();
        UiPage page = Ui.Page("Focus", "Subtitle", Ui.Text("Hello"), Ui.Button("go", "Go", UiButtonVariant.Primary));

        UiNode rendered = await context.RenderAsync(new SamplePage(Nav, page), TestContext.Current.CancellationToken);

        var result = Assert.IsType<UiPage>(rendered);
        Assert.NotSame(page, result);
        Assert.Equal("Focus", result.Title);
        Assert.Equal("Subtitle", result.Subtitle);
        Assert.Equal(
            [Ui.Text("Hello"), Ui.Button("go", "Go", UiButtonVariant.Primary)],
            result.Children);
    }

    [Fact]
    public async Task RenderAsync_DuplicateElementIds_ThrowsWithAllErrors()
    {
        var context = new FakePluginContext();
        UiPage page = Ui.Page("Focus", Ui.Button("same", "A"), Ui.Button("same", "B"));

        var ex = await Assert.ThrowsAsync<UiValidationException>(async () => await context.RenderAsync(new SamplePage(Nav, page), TestContext.Current.CancellationToken));

        Assert.Equal(["Element id 'same' is used more than once."], ex.Errors);
        Assert.Equal("The UI tree is invalid (1 error(s)): Element id 'same' is used more than once.", ex.Message);
    }

    [Fact]
    public async Task RenderAsync_InvalidNavigationAndTree_ReportsBoth()
    {
        var context = new FakePluginContext();
        var page = new UiPage("", null, []);
        var nav = new UiNavigationItem(new string('n', 33), UiIcon.Timer);

        var ex = await Assert.ThrowsAsync<UiValidationException>(async () => await context.RenderAsync(new SamplePage(nav, page), TestContext.Current.CancellationToken));

        Assert.Equal(["Navigation title must be 1-32 printable characters.", "UiPage.Title is required."], ex.Errors);
    }

    [Fact]
    public async Task RenderAsync_Widget_UsesCurrentSession()
    {
        var context = new FakePluginContext();
        context.FakeSession.Current = PluginTestData.ActiveSession(TimeSpan.FromMinutes(42));

        UiNode rendered = await context.RenderAsync(new SampleWidget(), TestContext.Current.CancellationToken);

        var card = Assert.IsType<UiCard>(rendered);
        Assert.Equal("Time left", card.Title);
        Assert.Equal([Ui.Text("42 min")], card.Children);
    }

    [Fact]
    public async Task RenderAsync_SettingsAndBlockScreen_AreValidated()
    {
        var context = new FakePluginContext();
        UiNode good = Ui.Toggle("t", "Enabled", true);

        Assert.Equal(good, await context.RenderAsync(new SampleSettings(good), TestContext.Current.CancellationToken));
        Assert.Equal(good, await context.RenderAsync(new SampleBlockScreen(good), TestContext.Current.CancellationToken));
        var settings = await Assert.ThrowsAsync<UiValidationException>(
            async () => await context.RenderAsync(new SampleSettings(Ui.Page("P")), TestContext.Current.CancellationToken));
        var block = await Assert.ThrowsAsync<UiValidationException>(
            async () => await context.RenderAsync(new SampleBlockScreen(Ui.Page("P")), TestContext.Current.CancellationToken));

        Assert.Equal(["UiPage is only allowed for IUiPagePlugin."], settings.Errors);
        Assert.Equal(["UiPage is only allowed for IUiPagePlugin."], block.Errors);
    }

    [Fact]
    public async Task RenderAsync_UndefinedEnum_FailsValidation()
    {
        var context = new FakePluginContext();
        UiNode bad = Ui.Text("x", (UiTextStyle)99);

        var ex = await Assert.ThrowsAsync<UiValidationException>(async () => await context.RenderAsync(new SampleSettings(bad), TestContext.Current.CancellationToken));

        Assert.Equal(["UiText.Style has an undefined value."], ex.Errors);
    }

    [Fact]
    public async Task RenderAsync_WithoutRenderInterface_Throws()
    {
        var context = new FakePluginContext();

        await Assert.ThrowsAsync<ArgumentException>(async () => await context.RenderAsync(new BareContribution(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendActionAsync_DeliversActionAndCountsRenderRequests()
    {
        var context = new FakePluginContext();
        var page = new SamplePage(Nav, Ui.Page("P"));
        Assert.Equal(0, context.RenderRequests);

        await context.SendActionAsync(page, UiAction.Click("go"), TestContext.Current.CancellationToken);
        await context.SendActionAsync(page, UiAction.Toggle("t", true), TestContext.Current.CancellationToken);

        Assert.Equal(2, context.RenderRequests);
        Assert.Equal(["go", "t"], page.Actions.Select(a => a.ElementId));
        Assert.Equal([UiActionKind.Click, UiActionKind.Toggle], page.Actions.Select(a => a.Kind));
        Assert.Equal("true", page.Actions[1].Value);
    }

    [Fact]
    public async Task SendActionAsync_DefaultImplementation_DoesNotRequestRender()
    {
        var context = new FakePluginContext();

        await context.SendActionAsync(new SampleWidget(), UiAction.Click("go"), TestContext.Current.CancellationToken);

        Assert.Equal(0, context.RenderRequests);
    }

    [Fact]
    public void PluginTestData_Process_UsesGameDefaults()
    {
        var process = PluginTestData.Process();

        Assert.Equal(4242, process.ProcessId);
        Assert.Equal("game.exe", process.ImageName);
        Assert.Equal(@"C:\Games\game.exe", process.ExecutablePath);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), process.StartedAt);
        Assert.Equal(@"C:\Games\other.exe", PluginTestData.Process("other.exe", 7).ExecutablePath);
        Assert.Equal(7, PluginTestData.Process("other.exe", 7).ProcessId);
    }

    [Fact]
    public void PluginTestData_ActiveSession_DescribesRunningSession()
    {
        SessionSnapshot snapshot = PluginTestData.ActiveSession(TimeSpan.FromMinutes(10), passesLeft: 1);

        Assert.Equal(SessionPhase.Active, snapshot.Phase);
        Assert.True(snapshot.IsSessionRunning);
        Assert.NotNull(snapshot.SessionId);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Remaining);
        Assert.Equal(1, snapshot.EmergencyPassesLeftToday);
        Assert.Equal(2, snapshot.EmergencyPassesPerDay);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.EmergencyPassDuration);
        Assert.Null(snapshot.EmergencyPassRemaining);
    }

    [Fact]
    public void FakeSession_DefaultsToIdle()
    {
        var context = new FakePluginContext();

        Assert.Same(SessionSnapshot.Idle, context.Session.Current);
    }

    [Fact]
    public void FakeLogger_RecordsEntriesAndTruncates()
    {
        var context = new FakePluginContext();

        context.Logger.Info("hello");
        context.Logger.Error(new string('x', 2500), new InvalidOperationException("bad"));

        Assert.Equal(2, context.FakeLogger.Entries.Count);
        Assert.Equal(("hello", PluginLogLevel.Information), (context.FakeLogger.Entries[0].Message, context.FakeLogger.Entries[0].Level));
        Assert.Equal(2000, context.FakeLogger.Entries[1].Message.Length);
        Assert.Equal("bad", context.FakeLogger.Entries[1].Exception?.Message);
    }
}
