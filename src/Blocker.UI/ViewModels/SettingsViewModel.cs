using System.Collections.ObjectModel;
using System.Reflection;
using Blocker.Contracts;
using Blocker.UI.Controls.Metaballs;
using Blocker.UI.Controls.Services;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>Settings page: effects quality, about information and plugin settings sections.</summary>
internal sealed partial class SettingsViewModel : ServiceViewModel
{
    private readonly IUiPreferences _preferences;

    [ObservableProperty]
    private MetaballQuality _effects;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="preferences">Per-user UI preferences.</param>
    /// <param name="snackbar">Snackbars.</param>
    public SettingsViewModel(IServiceConnection connection, IUiPreferences preferences, ISnackbarService snackbar)
        : base(connection, snackbar)
    {
        _preferences = preferences;
        _effects = preferences.Effects;
        connection.ConnectionChanged += OnConnectionChanged;
        connection.SessionChanged += (_, _) => _ = RefreshSectionsAsync(CancellationToken.None);
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Gets the effects choices.</summary>
    public IReadOnlyList<MetaballQuality> EffectsOptions { get; } = [MetaballQuality.High, MetaballQuality.Low, MetaballQuality.Off];

    /// <summary>Gets the plugin settings sections.</summary>
    public ObservableCollection<ContributionViewModel> Sections { get; } = [];

    /// <summary>Gets whether any plugin settings section is shown.</summary>
    public bool HasSections => Sections.Count > 0;

    /// <summary>Gets the application name and version.</summary>
    public string VersionText { get; } = DescribeVersion();

    /// <summary>Gets a description of the effects choice, shown under the selector.</summary>
    public string EffectsDescription => Effects switch
    {
        MetaballQuality.High => "Smooth metaball field and animations. Recommended.",
        MetaballQuality.Low => "Lower resolution and frame rate to save battery and GPU time.",
        _ => "No animated effects. The interface stays static.",
    };

    partial void OnEffectsChanged(MetaballQuality value)
    {
        _preferences.Effects = value;
        OnPropertyChanged(nameof(EffectsDescription));
    }

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
