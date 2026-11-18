using Blocker.Contracts;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class AllowlistViewModelTests
{
    private readonly FakeServiceConnection _connection = new();
    private readonly FakeSnackbarService _snackbar = new();

    private static InstalledAppDto App(string name, string path, string? hash = null) =>
        new(name, path, hash, null, null, AppSourceDto.StartMenu | AppSourceDto.Running);

    private AllowlistViewModel CreateWithApps()
    {
        _connection.SetConnected(false);
        AllowlistViewModel vm = new(_connection, _snackbar);
        vm.LoadApps(
            [
                App("Visual Studio Code", @"C:\Apps\Code\code.exe", "aa"),
                App("Firefox", @"C:\Apps\Firefox\firefox.exe", "bb"),
                App("Notepad", @"C:\Windows\notepad.exe", "cc"),
                App("Notion", @"C:\Apps\Notion\notion.exe", "dd"),
            ],
            []);
        return vm;
    }

    [Fact]
    public void Apps_are_sorted_by_name_and_search_filters_by_name_or_path()
    {
        AllowlistViewModel vm = CreateWithApps();
        Assert.Equal(["Firefox", "Notepad", "Notion", "Visual Studio Code"], vm.FilteredApps.Select(a => a.DisplayName));

        vm.SearchText = "not";
        Assert.Equal(["Notepad", "Notion"], vm.FilteredApps.Select(a => a.DisplayName));

        vm.SearchText = @"apps\firefox";
        Assert.Equal(["Firefox"], vm.FilteredApps.Select(a => a.DisplayName));

        vm.SearchText = "zzz";
        Assert.True(vm.HasNoApps);
    }

    [Fact]
    public void Select_all_filtered_selects_only_the_visible_apps_and_toggles_back()
    {
        AllowlistViewModel vm = CreateWithApps();
        vm.SearchText = "not";

        vm.SelectAllFilteredCommand.Execute(null);

        Assert.Equal(2, vm.SelectedCount);
        Assert.True(vm.AreAllFilteredSelected);
        Assert.Equal(["Notepad", "Notion"], vm.BuildSelectedEntries().Select(e => e.DisplayName).Order());

        vm.SelectAllFilteredCommand.Execute(null);

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.AreAllFilteredSelected);
    }

    [Fact]
    public void Selection_survives_changing_the_search_and_updates_the_summary()
    {
        AllowlistViewModel vm = CreateWithApps();
        vm.FilteredApps.First(a => a.DisplayName == "Firefox").IsSelected = true;
        vm.SearchText = "visual";

        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("1 of 4 apps selected", vm.SelectionSummary);
        Assert.False(vm.AreAllFilteredSelected);

        vm.SelectAllFilteredCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public void Stored_entries_are_selected_and_kept_even_when_the_scan_does_not_find_them()
    {
        AllowlistViewModel vm = new(_connection, _snackbar);
        vm.LoadApps(
            [App("Firefox", @"C:\Apps\Firefox\firefox.exe", "bb")],
            [
                new AllowlistEntryDto("Firefox", @"D:\moved\firefox.exe", "BB", null),
                new AllowlistEntryDto("Portable tool", @"E:\tools\tool.exe", "ee", null),
            ]);

        Assert.Equal(2, vm.SelectedCount);
        Assert.Contains(vm.BuildSelectedEntries(), e => e.DisplayName == "Portable tool");
        Assert.Equal(2, vm.FilteredApps.Count);
    }

    [Fact]
    public void Selection_changed_event_fires_for_apps_and_domains()
    {
        AllowlistViewModel vm = CreateWithApps();
        int raised = 0;
        vm.SelectionChanged += (_, _) => raised++;

        vm.FilteredApps[0].IsSelected = true;
        vm.NewDomain = "example.com";
        vm.AddDomainCommand.Execute(null);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Read_only_during_a_session_locks_every_editing_command()
    {
        AllowlistViewModel vm = CreateWithApps();
        vm.NewDomain = "example.com";
        Assert.True(vm.AddDomainCommand.CanExecute(null));
        Assert.True(vm.SelectAllFilteredCommand.CanExecute(null));

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));

        Assert.True(vm.IsReadOnly);
        Assert.True(vm.IsLocked);
        Assert.False(vm.CanEditSelection);
        Assert.False(vm.AddDomainCommand.CanExecute(null));
        Assert.False(vm.RemoveDomainCommand.CanExecute("example.com"));
        Assert.False(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));

        _connection.Push(Blocker.Sdk.Session.SessionSnapshot.Idle);

        Assert.False(vm.IsReadOnly);
        Assert.True(vm.AddDomainCommand.CanExecute(null));
    }

    [Fact]
    public void Domains_are_validated_normalized_and_deduplicated()
    {
        AllowlistViewModel vm = CreateWithApps();

        vm.NewDomain = "  Example.COM ";
        Assert.True(vm.IsDomainValid);
        vm.AddDomainCommand.Execute(null);
        Assert.Equal(["example.com"], vm.Domains);
        Assert.Equal(string.Empty, vm.NewDomain);

        vm.NewDomain = "example.com";
        Assert.False(vm.IsDomainValid);
        Assert.True(vm.HasDomainError);
        Assert.Contains("already", vm.DomainHint);
        Assert.False(vm.AddDomainCommand.CanExecute(null));

        vm.NewDomain = "*.docs.example.org";
        vm.AddDomainCommand.Execute(null);
        Assert.Equal(["example.com", "*.docs.example.org"], vm.Domains);

        vm.RemoveDomainCommand.Execute("example.com");
        Assert.Equal(["*.docs.example.org"], vm.Domains);
    }

    [Fact]
    public void Invalid_domain_shows_an_error_and_cannot_be_added()
    {
        AllowlistViewModel vm = CreateWithApps();

        vm.NewDomain = "https://example.com/path";

        Assert.False(vm.IsDomainValid);
        Assert.True(vm.HasDomainError);
        Assert.False(vm.AddDomainCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_sends_selected_apps_and_domains()
    {
        AllowlistViewModel vm = CreateWithApps();
        _connection.SetConnected(true);
        vm.FilteredApps.First(a => a.DisplayName == "Notepad").IsSelected = true;
        vm.NewDomain = "example.com";
        vm.AddDomainCommand.Execute(null);
        AllowlistDto? sent = null;
        _connection.On<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, a =>
        {
            sent = a;
            return new OperationResponse(true, []);
        });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(sent);
        Assert.Equal(["Notepad"], sent.Apps.Select(a => a.DisplayName));
        Assert.Equal(["example.com"], sent.Domains);
        Assert.Contains(_snackbar.Shown, s => s.Title == "Allowlist saved");
    }

    [Fact]
    public async Task Scan_fills_the_picker_from_the_service_and_marks_stored_entries()
    {
        _connection.On<ScanAppsResponse>(IpcMethods.AllowlistScanApps, () => new ScanAppsResponse([App("Firefox", @"C:\f.exe", "bb"), App("Notepad", @"C:\n.exe", "cc")]));
        _connection.On<AllowlistDto>(IpcMethods.AllowlistGet, () => new AllowlistDto([new AllowlistEntryDto("Notepad", @"C:\n.exe", "cc", null)], ["example.com"]));
        _connection.SetConnected(false);
        AllowlistViewModel vm = new(_connection, _snackbar);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.FilteredApps.Count);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal(["example.com"], vm.Domains);
        Assert.False(vm.IsScanning);
    }
}
