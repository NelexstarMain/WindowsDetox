using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Controls.Services;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class AppSetsViewModelTests
{
    private static readonly AllowlistEntryDto Firefox = new("Firefox", @"C:\f.exe", "bb", null);
    private static readonly AllowlistEntryDto Notepad = new("Notepad", @"C:\n.exe", "cc", null);
    private static readonly AllowlistEntryDto Code = new("Code", @"C:\c.exe", "aa", null);

    private readonly FakeServiceConnection _connection = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeSnackbarService _snackbar = new();
    private readonly List<AppSetsDto> _saved = [];
    private AppSetsDto _stored = new([new("Browse", [Firefox], ["github.com"]), new("Edit", [Code, Notepad], [])]);
    private bool _acceptSave = true;

    private AppSetsViewModel Create(out AllowlistViewModel allowlist)
    {
        _connection.On<ScanAppsResponse>(IpcMethods.AllowlistScanApps, () => new ScanAppsResponse(
            [
                new InstalledAppDto("Code", @"C:\c.exe", "aa", null, null, AppSourceDto.StartMenu),
                new InstalledAppDto("Firefox", @"C:\f.exe", "bb", null, null, AppSourceDto.StartMenu),
                new InstalledAppDto("Notepad", @"C:\n.exe", "cc", null, null, AppSourceDto.StartMenu),
            ]));
        _connection.On<AllowlistDto>(IpcMethods.AllowlistGet, () => new AllowlistDto([], []));
        _connection.On<AppSetsDto>(IpcMethods.AppSetsGet, () => _stored);
        _connection.On<AppSetsDto, OperationResponse>(IpcMethods.AppSetsSet, sets =>
        {
            if (!_acceptSave)
            {
                return new OperationResponse(false, ["Refused."]);
            }

            _saved.Add(sets);
            return new OperationResponse(true, []);
        });
        allowlist = new AllowlistViewModel(_connection, _snackbar);
        return new AppSetsViewModel(_connection, _snackbar, _dialogs, allowlist);
    }

    private static AppEntryViewModel App(AllowlistViewModel allowlist, string name) => allowlist.FilteredApps.First(a => a.DisplayName == name);

    [Fact]
    public void Saved_sets_are_read_from_the_service_in_order()
    {
        AppSetsViewModel vm = Create(out _);

        Assert.Equal(["Browse", "Edit"], vm.Sets.Select(s => s.Name));
        Assert.False(vm.HasNoSets);
    }

    [Fact]
    public void Applying_a_set_replaces_the_apps_and_the_domains()
    {
        AppSetsViewModel vm = Create(out AllowlistViewModel allowlist);
        App(allowlist, "Notepad").IsSelected = true;
        allowlist.NewDomain = "example.com";
        allowlist.AddDomainCommand.Execute(null);

        vm.ApplySetCommand.Execute(vm.Sets[0]);

        Assert.Equal(["Firefox"], allowlist.BuildSelectedEntries().Select(e => e.DisplayName));
        Assert.Equal(["github.com"], allowlist.Domains);
    }

    [Fact]
    public void Applying_a_set_keeps_an_app_the_scan_did_not_find()
    {
        _stored = new AppSetsDto([new("Portable", [new AllowlistEntryDto("Portable", @"D:\p.exe", null, null)], [])]);
        AppSetsViewModel vm = Create(out AllowlistViewModel allowlist);

        vm.ApplySetCommand.Execute(vm.Sets[0]);

        Assert.Equal(["Portable"], allowlist.BuildSelectedEntries().Select(e => e.DisplayName));
    }

    [Fact]
    public void The_chip_equal_to_the_selection_is_highlighted_and_follows_changes()
    {
        AppSetsViewModel vm = Create(out AllowlistViewModel allowlist);
        Assert.All(vm.Sets, s => Assert.False(s.IsCurrent));

        vm.ApplySetCommand.Execute(vm.Sets[0]);
        Assert.True(vm.Sets[0].IsCurrent);
        Assert.False(vm.Sets[1].IsCurrent);

        App(allowlist, "Notepad").IsSelected = true;
        Assert.False(vm.Sets[0].IsCurrent);

        App(allowlist, "Notepad").IsSelected = false;
        Assert.True(vm.Sets[0].IsCurrent);

        allowlist.RemoveDomainCommand.Execute("github.com");
        Assert.False(vm.Sets[0].IsCurrent);
    }

    [Fact]
    public async Task Saving_stores_the_current_selection_under_the_typed_name()
    {
        AppSetsViewModel vm = Create(out AllowlistViewModel allowlist);
        App(allowlist, "Code").IsSelected = true;
        allowlist.NewDomain = "docs.rs";
        allowlist.AddDomainCommand.Execute(null);

        vm.BeginSaveCommand.Execute(null);
        Assert.True(vm.IsNaming);
        vm.NewName = "  Rust  ";
        await vm.ConfirmSaveCommand.ExecuteAsync(null);

        AppSetDto added = _saved.Single().Sets[^1];
        Assert.Equal("Rust", added.Name);
        Assert.Equal(["Code"], added.Apps.Select(a => a.DisplayName));
        Assert.Equal(["docs.rs"], added.Domains);
        Assert.False(vm.IsNaming);
        Assert.Equal(["Browse", "Edit", "Rust"], vm.Sets.Select(s => s.Name));
        Assert.True(vm.Sets[^1].IsCurrent);
        Assert.Empty(_dialogs.Confirmations);
    }

    [Fact]
    public void An_empty_name_cannot_be_saved()
    {
        AppSetsViewModel vm = Create(out _);
        vm.BeginSaveCommand.Execute(null);

        vm.NewName = "   ";

        Assert.False(vm.ConfirmSaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Saving_under_an_existing_name_asks_before_replacing_it()
    {
        AppSetsViewModel vm = Create(out AllowlistViewModel allowlist);
        App(allowlist, "Notepad").IsSelected = true;
        vm.BeginSaveCommand.Execute(null);
        vm.NewName = "browse";
        Assert.True(vm.NameExists);

        await vm.ConfirmSaveCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Confirmations);
        AppSetsDto saved = _saved.Single();
        Assert.Equal(["Browse", "Edit"], saved.Sets.Select(s => s.Name));
        Assert.Equal(["Notepad"], saved.Sets[0].Apps.Select(a => a.DisplayName));
    }

    [Fact]
    public async Task Declining_the_overwrite_changes_nothing_and_keeps_the_name_box_open()
    {
        AppSetsViewModel vm = Create(out _);
        _dialogs.ConfirmResult = false;
        vm.BeginSaveCommand.Execute(null);
        vm.NewName = "Browse";

        await vm.ConfirmSaveCommand.ExecuteAsync(null);

        Assert.Empty(_saved);
        Assert.True(vm.IsNaming);
    }

    [Fact]
    public async Task A_refused_save_keeps_the_name_box_open_and_shows_why()
    {
        AppSetsViewModel vm = Create(out _);
        _acceptSave = false;
        vm.BeginSaveCommand.Execute(null);
        vm.NewName = "New";

        await vm.ConfirmSaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsNaming);
        Assert.Equal(2, vm.Sets.Count);
        Assert.Contains(_snackbar.Shown, s => s.Severity == NotificationSeverity.Error && s.Message.Contains("Refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_set_asks_first_and_then_saves_the_rest()
    {
        AppSetsViewModel vm = Create(out _);

        await vm.RemoveSetCommand.ExecuteAsync(vm.Sets[0]);

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal(["Edit"], _saved.Single().Sets.Select(s => s.Name));
        Assert.Equal(["Edit"], vm.Sets.Select(s => s.Name));
    }

    [Fact]
    public async Task Declining_the_removal_keeps_the_set()
    {
        AppSetsViewModel vm = Create(out _);
        _dialogs.ConfirmResult = false;

        await vm.RemoveSetCommand.ExecuteAsync(vm.Sets[0]);

        Assert.Empty(_saved);
        Assert.Equal(2, vm.Sets.Count);
    }

    [Fact]
    public void Everything_is_disabled_while_a_session_runs()
    {
        AppSetsViewModel vm = Create(out _);
        vm.BeginSaveCommand.Execute(null);

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));

        Assert.False(vm.CanEdit);
        Assert.False(vm.IsNaming);
        Assert.False(vm.ApplySetCommand.CanExecute(vm.Sets[0]));
        Assert.False(vm.RemoveSetCommand.CanExecute(vm.Sets[0]));
        Assert.False(vm.BeginSaveCommand.CanExecute(null));
    }
}
