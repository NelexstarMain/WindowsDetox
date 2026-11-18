using Blocker.Contracts;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class PluginsViewModelTests : IDisposable
{
    private readonly FakeServiceConnection _connection = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeAppDialogs _appDialogs = new();
    private readonly FakeFileDialogs _files = new();
    private readonly FakeSnackbarService _snackbar = new();
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("blocker-ui-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    private static PluginInfoDto Info(string id, bool enabled = true, bool faulted = false, params string[] capabilities) =>
        new(id, id.ToUpperInvariant(), "1.0.0", "Someone", enabled, capabilities, faulted);

    private PluginsViewModel Create(params PluginInfoDto[] plugins)
    {
        _connection.On<ListPluginsResponse>(IpcMethods.PluginsList, () => new ListPluginsResponse(plugins));
        return new PluginsViewModel(_connection, _dialogs, _appDialogs, _files, _snackbar);
    }

    [Fact]
    public void Plugins_load_with_state_and_privacy_sensitive_capabilities_first()
    {
        PluginsViewModel vm = Create(
            Info("focus.timer", capabilities: ["ui.dashboard", "events.window.title", "tick"]),
            Info("off.plugin", enabled: false),
            Info("broken.plugin", faulted: true));

        Assert.Equal([PluginState.Enabled, PluginState.Disabled, PluginState.Faulted], vm.Plugins.Select(p => p.State));
        PluginItemViewModel first = vm.Plugins[0];
        Assert.True(first.HasPrivacySensitiveCapability);
        Assert.Equal("events.window.title", first.Capabilities[0].Name);
        Assert.True(first.Capabilities[0].IsPrivacySensitive);
        Assert.Equal("Reads window titles", first.Capabilities[0].Label);
        Assert.All(first.Capabilities.Skip(1), c => Assert.False(c.IsPrivacySensitive));
        Assert.Equal("Disable", first.ToggleLabel);
        Assert.Equal("Enable", vm.Plugins[1].ToggleLabel);
        Assert.Equal("Faulted", vm.Plugins[2].StateLabel);
    }

    [Fact]
    public void Every_mutation_is_disabled_during_a_session_with_an_explanation()
    {
        PluginsViewModel vm = Create(Info("a.plugin"));
        PluginItemViewModel plugin = vm.Plugins[0];
        Assert.True(vm.CanMutate);
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.True(vm.ToggleEnabledCommand.CanExecute(plugin));
        Assert.True(vm.RemoveCommand.CanExecute(plugin));

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));

        Assert.True(vm.IsLocked);
        Assert.False(vm.CanMutate);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.ToggleEnabledCommand.CanExecute(plugin));
        Assert.False(vm.RemoveCommand.CanExecute(plugin));
        Assert.Contains("while a session is running", vm.LockMessage);

        _connection.Push(Blocker.Sdk.Session.SessionSnapshot.Idle);

        Assert.True(vm.CanMutate);
    }

    [Fact]
    public void Mutations_are_disabled_while_the_service_is_unreachable()
    {
        PluginsViewModel vm = Create(Info("a.plugin"));

        _connection.SetConnected(false);

        Assert.False(vm.CanMutate);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Starter_template_can_be_saved_even_during_a_session_without_any_service_call()
    {
        PluginsViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        string path = Path.Combine(_tempDirectory, "starter.zip");
        _files.SavePath = path;

        await vm.DownloadTemplateCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(path))
        {
            Assert.Contains(zip.Entries, e => e.Name == "plugin.json");
        }

        Assert.Equal(0, _connection.CallCount(IpcMethods.PluginsDownloadTemplate));
        Assert.Contains(_snackbar.Shown, s => s.Title == "Starter template saved");
    }

    [Fact]
    public async Task Cancelled_save_dialog_writes_nothing()
    {
        PluginsViewModel vm = Create();
        _files.SavePath = null;

        await vm.DownloadTemplateCommand.ExecuteAsync(null);

        Assert.Empty(_snackbar.Shown);
    }

    [Fact]
    public async Task Install_sends_the_zip_as_base64_and_reports_failure_stage_by_stage()
    {
        PluginsViewModel vm = Create();
        string path = Path.Combine(_tempDirectory, "plugin.zip");
        byte[] zip = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(path, zip, TestContext.Current.CancellationToken);
        _files.OpenPath = path;
        string? sent = null;
        _connection.On<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, r =>
        {
            sent = r.PackageBase64;
            return new InstallPluginResponse(false, null, ["Plugin.cs:12: BLK003: System.IO is not allowed.", "Failed: Tests.Works: expected 1 but got 2"]);
        });

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(Convert.ToBase64String(zip), sent);
        PluginInstallReport report = Assert.Single(_appDialogs.Reports);
        Assert.False(report.Succeeded);
        Assert.Equal(InstallStageStatus.Failed, report.Stages.Single(s => s.Name == "Tests").Status);
        Assert.DoesNotContain(_snackbar.Shown, s => s.Title == "Plugin installed");
    }

    [Fact]
    public async Task Successful_install_refreshes_the_list_and_raises_plugins_changed()
    {
        string path = Path.Combine(_tempDirectory, "plugin.zip");
        await File.WriteAllBytesAsync(path, [9], TestContext.Current.CancellationToken);
        _files.OpenPath = path;
        List<PluginInfoDto> installed = [];
        _connection.On<ListPluginsResponse>(IpcMethods.PluginsList, () => new ListPluginsResponse([.. installed]));
        _connection.On<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, _ =>
        {
            installed.Add(Info("new.plugin"));
            return new InstallPluginResponse(true, "new.plugin", []);
        });
        PluginsViewModel vm = new(_connection, _dialogs, _appDialogs, _files, _snackbar);
        int changed = 0;
        vm.PluginsChanged += (_, _) => changed++;

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["new.plugin"], vm.Plugins.Select(p => p.Id));
        Assert.True(_appDialogs.Reports[0].Succeeded);
        Assert.Equal(1, changed);
        Assert.Contains(_snackbar.Shown, s => s.Title == "Plugin installed");
    }

    [Fact]
    public async Task Installing_without_administrator_rights_shows_a_helpful_message()
    {
        PluginsViewModel vm = Create();
        string path = Path.Combine(_tempDirectory, "plugin.zip");
        await File.WriteAllBytesAsync(path, [9], TestContext.Current.CancellationToken);
        _files.OpenPath = path;
        _connection.On<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, _ => throw new IpcException(IpcErrorCode.Unauthorized, "no"));

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Contains(_snackbar.Shown, s => s.Message.Contains("administrator", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_appDialogs.Reports);
    }

    [Fact]
    public async Task Oversized_package_is_rejected_before_any_service_call()
    {
        PluginsViewModel vm = Create();
        string path = Path.Combine(_tempDirectory, "big.zip");
        await File.WriteAllBytesAsync(path, new byte[PluginsViewModel.MaxPackageBytes + 1], TestContext.Current.CancellationToken);
        _files.OpenPath = path;

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(0, _connection.CallCount(IpcMethods.PluginsInstall));
        Assert.Contains(_snackbar.Shown, s => s.Title == "Package too large");
    }

    [Fact]
    public async Task Toggle_flips_the_state_after_the_service_confirms()
    {
        PluginsViewModel vm = Create(Info("a.plugin"));
        _connection.On<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, r => new OperationResponse(true, []));
        PluginItemViewModel plugin = vm.Plugins[0];

        await vm.ToggleEnabledCommand.ExecuteAsync(plugin);

        Assert.Equal(PluginState.Disabled, plugin.State);
        Assert.Equal("Enable", plugin.ToggleLabel);
        SetPluginEnabledRequest request = (SetPluginEnabledRequest)_connection.Calls.Last(c => c.Method == IpcMethods.PluginsSetEnabled).Request!;
        Assert.False(request.Enabled);
    }

    [Fact]
    public async Task Remove_asks_first_and_removes_on_success()
    {
        PluginsViewModel vm = Create(Info("a.plugin"), Info("b.plugin"));
        _connection.On<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, _ => new OperationResponse(true, []));

        _dialogs.ConfirmResult = false;
        await vm.RemoveCommand.ExecuteAsync(vm.Plugins[0]);
        Assert.Equal(2, vm.Plugins.Count);
        Assert.Equal(0, _connection.CallCount(IpcMethods.PluginsRemove));

        _dialogs.ConfirmResult = true;
        await vm.RemoveCommand.ExecuteAsync(vm.Plugins[0]);
        Assert.Equal(["b.plugin"], vm.Plugins.Select(p => p.Id));
    }
}
