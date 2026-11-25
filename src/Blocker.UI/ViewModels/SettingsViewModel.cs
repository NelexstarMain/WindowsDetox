using System.Collections.ObjectModel;
using System.Reflection;
using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>Settings page: the block notice switch, about information and plugin settings sections.</summary>
internal sealed partial class SettingsViewModel : ServiceViewModel
{
    private const string BlockNoticeKey = "blockNotice.show";

    private bool _applyingStored;

    [ObservableProperty]
    private bool _showBlockNotice = true;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="snackbar">Snackbars.</param>
    public SettingsViewModel(IServiceConnection connection, ISnackbarService snackbar)
        : base(connection, snackbar)
    {
        connection.ConnectionChanged += OnConnectionChanged;
        connection.SessionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanChangeBlockNotice));
            _ = RefreshSectionsAsync(CancellationToken.None);
        };
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Gets the plugin settings sections.</summary>
    public ObservableCollection<ContributionViewModel> Sections { get; } = [];

    /// <summary>Gets whether any plugin settings section is shown.</summary>
    public bool HasSections => Sections.Count > 0;

    /// <summary>Gets whether the block notice switch can be flipped: the service refuses settings changes during a session.</summary>
    public bool CanChangeBlockNotice => Connection.IsConnected && !Connection.Session.IsSessionRunning;

    /// <summary>Gets the application name and version.</summary>
    public string VersionText { get; } = DescribeVersion();

    /// <summary>Reloads the plugin settings sections.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the sections are current.</returns>
    [RelayCommand]
    public async Task RefreshSectionsAsync(CancellationToken cancellationToken)
    {
        if (!Connection.IsConnected)
        {
            return;
        }

        GetContributionsResponse? response = null;
        if (await TryAsync(async () => response = await Connection.GetContributionsAsync(UiSlot.SettingsSection, cancellationToken), "Could not load plugin settings") && response is not null)
        {
            ContributionViewModel.Synchronize(Sections, response.Items);
            OnPropertyChanged(nameof(HasSections));
        }
    }

    [RelayCommand]
    private async Task SendSectionActionAsync(ContributionAction? action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return;
        }

        bool sent = await TryAsync(
            async () => await Connection.SendActionAsync(action.Contribution.PluginId, action.Contribution.ContributionId, action.Action, cancellationToken),
            "Plugin setting failed");
        if (sent)
        {
            await RefreshSectionsAsync(cancellationToken);
        }
    }

    partial void OnShowBlockNoticeChanged(bool value)
    {
        if (!_applyingStored)
        {
            _ = SaveBlockNoticeAsync(value);
        }
    }

    private async Task SaveBlockNoticeAsync(bool show)
    {
        OperationResponse? response = null;
        bool sent = await TryAsync(
            async () => response = await Connection.SetSettingsAsync(new SettingsDto(new Dictionary<string, string> { [BlockNoticeKey] = show ? "on" : "off" }), CancellationToken.None),
            "Could not save the setting");
        if (sent && response is { Success: true })
        {
            return;
        }

        if (response is { Success: false })
        {
            Snackbar.Show("Could not save the setting", string.Join(" ", response.Errors), NotificationSeverity.Error);
        }

        ApplyStored(!show);
    }

    private async Task LoadSettingsAsync()
    {
        SettingsDto? settings = null;
        if (await TryAsync(async () => settings = await Connection.GetSettingsAsync(CancellationToken.None), "Could not load settings") && settings is not null)
        {
            ApplyStored(!settings.Values.TryGetValue(BlockNoticeKey, out string? value) || value != "off");
        }
    }

    private void ApplyStored(bool show)
    {
        _applyingStored = true;
        ShowBlockNotice = show;
        _applyingStored = false;
    }

    private static string DescribeVersion()
    {
        Assembly assembly = typeof(SettingsViewModel).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        return $"WindowsDetox {version}";
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanChangeBlockNotice));
        if (Connection.IsConnected)
        {
            _ = LoadSettingsAsync();
            _ = RefreshSectionsAsync(CancellationToken.None);
        }
    }
}
