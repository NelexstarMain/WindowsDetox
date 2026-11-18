using System.Collections.ObjectModel;
using System.Reflection;
using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>Settings page: about information and plugin settings sections.</summary>
internal sealed partial class SettingsViewModel : ServiceViewModel
{
    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="snackbar">Snackbars.</param>
    public SettingsViewModel(IServiceConnection connection, ISnackbarService snackbar)
        : base(connection, snackbar)
    {
        connection.ConnectionChanged += OnConnectionChanged;
        connection.SessionChanged += (_, _) => _ = RefreshSectionsAsync(CancellationToken.None);
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Gets the plugin settings sections.</summary>
    public ObservableCollection<ContributionViewModel> Sections { get; } = [];

    /// <summary>Gets whether any plugin settings section is shown.</summary>
    public bool HasSections => Sections.Count > 0;

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

    private static string DescribeVersion()
    {
        Assembly assembly = typeof(SettingsViewModel).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        return $"WindowsDetox {version}";
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (Connection.IsConnected)
        {
            _ = RefreshSectionsAsync(CancellationToken.None);
        }
    }
}
