using System.Collections.ObjectModel;
using Blocker.Contracts;
using Blocker.Plugin.Templates;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>
/// Plugins page: list, starter template download, install with a stage-by-stage report, enable/disable/remove.
/// Everything that changes plugins is disabled while a session runs.
/// </summary>
internal sealed partial class PluginsViewModel : ServiceViewModel
{
    /// <summary>Largest ZIP the page sends: the base64 form must fit the IPC message limit with room for the envelope.</summary>
    public static readonly int MaxPackageBytes = (IpcLimits.MaxMessageBytes - 4096) / 4 * 3;

    private const string ZipFilter = "Plugin package (*.zip)|*.zip";

    private readonly IDialogService _dialogs;
    private readonly IAppDialogs _appDialogs;
    private readonly IFileDialogs _files;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="dialogs">Confirmation dialogs.</param>
    /// <param name="appDialogs">Application dialogs.</param>
    /// <param name="files">File pickers.</param>
    /// <param name="snackbar">Snackbars.</param>
    public PluginsViewModel(
        IServiceConnection connection,
        IDialogService dialogs,
        IAppDialogs appDialogs,
        IFileDialogs files,
        ISnackbarService snackbar)
        : base(connection, snackbar)
    {
        _dialogs = dialogs;
        _appDialogs = appDialogs;
        _files = files;
        connection.SessionChanged += (_, _) => RefreshLock();
        connection.ConnectionChanged += OnConnectionChanged;
        RefreshLock();
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Raised after the set of installed or enabled plugins changed, so contributions can be reloaded.</summary>
    public event EventHandler? PluginsChanged;

    /// <summary>Gets the installed plugins.</summary>
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    /// <summary>Gets whether the list is empty.</summary>
    public bool HasNoPlugins => Plugins.Count == 0;

    /// <summary>Gets whether plugin changes are currently possible.</summary>
    public bool CanMutate => Connection.IsConnected && !IsLocked && !IsBusy;

    /// <summary>Gets the explanation shown while plugin changes are disabled.</summary>
    public string LockMessage => "Plugins can't be installed, removed or switched while a session is running. Download the starter template any time.";

    /// <summary>Gets the note about administrator rights.</summary>
    public string AdminNote => "Installing, removing and switching plugins needs administrator rights.";

    partial void OnIsLockedChanged(bool value) => NotifyMutationState();

    partial void OnIsBusyChanged(bool value) => NotifyMutationState();

    /// <summary>Reloads the plugin list.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the list is current.</returns>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ListPluginsResponse? response = null;
        if (!await TryAsync(async () => response = await Connection.ListPluginsAsync(cancellationToken), "Could not load plugins") || response is null)
        {
            return;
        }

        Plugins.Clear();
        foreach (PluginInfoDto dto in response.Plugins)
        {
            Plugins.Add(new PluginItemViewModel(dto));
        }

        OnPropertyChanged(nameof(HasNoPlugins));
    }

    [RelayCommand]
    private async Task DownloadTemplateAsync(CancellationToken cancellationToken)
    {
        string? path = _files.PickSavePath("blocker-plugin-starter.zip", ZipFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await PluginStarterTemplate.WriteZipAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Snackbar.Show("Could not save the template", ex.Message, NotificationSeverity.Error);
            return;
        }

        Snackbar.Show("Starter template saved", path, NotificationSeverity.Success);
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        string? path = _files.PickOpenPath(ZipFilter);
        if (path is null)
        {
            return;
        }

        byte[] package;
        try
        {
            package = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Snackbar.Show("Could not read the file", ex.Message, NotificationSeverity.Error);
            return;
        }

        if (package.Length > MaxPackageBytes)
        {
            Snackbar.Show("Package too large", "Plugin packages must be smaller than 3 MB.", NotificationSeverity.Error);
            return;
        }

        IsBusy = true;
        try
        {
            InstallPluginResponse? response = null;
            string base64 = Convert.ToBase64String(package);
            if (!await TryAsync(async () => response = await Connection.InstallPluginAsync(base64, cancellationToken), "Install failed") || response is null)
            {
                return;
            }

            PluginInstallReport report = PluginInstallReportMapper.Map(response);
            await _appDialogs.ShowInstallReportAsync(report, cancellationToken);
            if (report.Succeeded)
            {
                Snackbar.Show("Plugin installed", report.PluginId ?? "The plugin is ready.", NotificationSeverity.Success);
                await RefreshAsync(cancellationToken);
                PluginsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ToggleEnabledAsync(PluginItemViewModel? plugin, CancellationToken cancellationToken)
    {
        if (plugin is null)
        {
            return;
        }

        bool enable = plugin.State != PluginState.Enabled;
        IsBusy = true;
        try
        {
            OperationResponse? response = null;
            if (!await TryAsync(async () => response = await Connection.SetPluginEnabledAsync(plugin.Id, enable, cancellationToken), "Could not change the plugin") || response is null)
            {
                return;
            }

            if (response.Success)
            {
                plugin.State = enable ? PluginState.Enabled : PluginState.Disabled;
                PluginsChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Snackbar.Show("Plugin unchanged", string.Join(' ', response.Errors), NotificationSeverity.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task RemoveAsync(PluginItemViewModel? plugin, CancellationToken cancellationToken)
    {
        if (plugin is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync($"Remove {plugin.Name}?", "The plugin and its stored data are deleted. You can install it again from its package.", "Remove plugin", "Keep it", cancellationToken))
        {
            return;
        }

        IsBusy = true;
        try
        {
            OperationResponse? response = null;
            if (!await TryAsync(async () => response = await Connection.RemovePluginAsync(plugin.Id, cancellationToken), "Could not remove the plugin") || response is null)
            {
                return;
            }

            if (response.Success)
            {
                Plugins.Remove(plugin);
                OnPropertyChanged(nameof(HasNoPlugins));
                PluginsChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Snackbar.Show("Plugin not removed", string.Join(' ', response.Errors), NotificationSeverity.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshLock() => IsLocked = Connection.Session.IsSessionRunning;

    private void NotifyMutationState()
    {
        OnPropertyChanged(nameof(CanMutate));
        InstallCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        NotifyMutationState();
        if (Connection.IsConnected)
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }
}
