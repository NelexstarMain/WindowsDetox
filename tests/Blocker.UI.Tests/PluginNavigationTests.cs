using Blocker.Contracts;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Tests;

public sealed class PluginNavigationTests
{
    private readonly FakeServiceConnection _connection = new();
    private readonly FakeTimeProvider _time = new();

    private static GetContributionsResponse Pages(params (string Plugin, string Id)[] pages) =>
        new(UiSlot.Page, [.. pages.Select(p => new UiContributionDto(p.Plugin, p.Id, p.Id, UiJson.Serialize(Ui.Page(p.Id)), null))]);

    private ShellViewModel CreateShell(params (string Plugin, string Id)[] pages)
    {
        _connection.On<GetContributionsRequest, GetContributionsResponse>(IpcMethods.UiGetContributions, _ => Pages(pages));
        PluginsViewModel plugins = new(_connection, new FakeDialogService(), new FakeAppDialogs(), new FakeFileDialogs(), new FakeSnackbarService());
        return new ShellViewModel(_connection, plugins, new FakeSnackbarService(), _time);
    }

    [Fact]
    public void A_plugin_opens_its_first_page()
    {
        ShellViewModel shell = CreateShell(("goals", "page-a"), ("insights", "page-b"), ("insights", "page-c"));

        ContributionViewModel? page = shell.FindPluginPage("insights");

        Assert.Equal("insights/page-b", page?.Key);
    }

    [Fact]
    public void A_plugin_without_a_page_has_none_to_open()
    {
        ShellViewModel shell = CreateShell(("goals", "page-a"));

        Assert.Null(shell.FindPluginPage("insights"));
        Assert.Null(shell.FindPluginPage("GOALS"));
    }

    [Fact]
    public void Pages_load_once_the_plugin_host_answers_after_the_ui_connected()
    {
        int calls = 0;
        _connection.On<GetContributionsRequest, GetContributionsResponse>(
            IpcMethods.UiGetContributions,
            _ => ++calls < 3 ? new GetContributionsResponse(UiSlot.Page, []) : Pages(("goals", "page-a")));
        PluginsViewModel plugins = new(_connection, new FakeDialogService(), new FakeAppDialogs(), new FakeFileDialogs(), new FakeSnackbarService());
        ShellViewModel shell = new(_connection, plugins, new FakeSnackbarService(), _time);
        Assert.Empty(shell.PluginPages);

        // Without a synchronization context the delay continuation (and the refresh after it) runs inside Advance.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(shell.PluginPages);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal("goals/page-a", Assert.Single(shell.PluginPages).Key);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void The_navigator_forwards_plugin_page_requests_to_the_shell()
    {
        Navigator navigator = new();
        List<string> requested = [];
        navigator.PluginPageRequested += (_, pluginId) => requested.Add(pluginId);

        navigator.GoToPluginPage("goals");
        navigator.GoTo(AppPage.Plugins);

        Assert.Equal(["goals"], requested);
    }
}
