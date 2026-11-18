using System.Collections.ObjectModel;
using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>Main window: connection banner, plugin navigation pages and service notifications as snackbars.</summary>
internal sealed partial class ShellViewModel : ServiceViewModel
{
    [ObservableProperty]
    private bool _isDisconnected;

    [ObservableProperty]
    private ContributionViewModel? _currentPluginPage;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="plugins">Plugins page, whose changes reload plugin pages.</param>
    /// <param name="snackbar">Snackbars.</param>
    public ShellViewModel(IServiceConnection connection, PluginsViewModel plugins, ISnackbarService snackbar)
        : base(connection, snackbar)
    {
        IsDisconnected = !connection.IsConnected;
        connection.ConnectionChanged += OnConnectionChanged;
        connection.NotificationReceived += OnNotification;
        plugins.PluginsChanged += (_, _) => _ = RefreshPagesAsync(CancellationToken.None);
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Gets the pages contributed by plugins; shown as extra navigation items.</summary>
    public ObservableCollection<ContributionViewModel> PluginPages { get; } = [];

    /// <summary>Gets the text of the disconnected banner.</summary>
    public string DisconnectedMessage => "Can't reach the Blocker service. Blocking continues if a session is running. Reconnecting automatically.";

    /// <summary>Reloads the plugin pages.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the pages are current.</returns>
    [RelayCommand]
    public async Task RefreshPagesAsync(CancellationToken cancellationToken)
    {
        if (!Connection.IsConnected)
        {
            return;
        }

        GetContributionsResponse? response = null;
        if (await TryAsync(async () => response = await Connection.GetContributionsAsync(UiSlot.Page, cancellationToken), "Could not load plugin pages") && response is not null)
        {
            ContributionViewModel.Synchronize(PluginPages, response.Items);
        }
    }

    /// <summary>Forwards an interaction on a plugin page to its plugin and reloads the page.</summary>
    /// <param name="action">The interaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the plugin handled the action.</returns>
    [RelayCommand]
    private async Task SendPageActionAsync(ContributionAction? action, CancellationToken cancellationToken)
    {
        if (action is null)
        {
            return;
        }

        bool sent = await TryAsync(
            async () => await Connection.SendActionAsync(action.Contribution.PluginId, action.Contribution.ContributionId, action.Action, cancellationToken),
            "Plugin action failed");
        if (sent)
        {
            await RefreshPagesAsync(cancellationToken);
        }
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        IsDisconnected = !Connection.IsConnected;
        if (Connection.IsConnected)
        {
            _ = RefreshPagesAsync(CancellationToken.None);
        }
    }

    private void OnNotification(object? sender, NotificationEventArgs e)
    {
        NotificationDto notification = e.Notification;
        NotificationSeverity tone = notification.Level switch
        {
            NotificationLevel.Warning => NotificationSeverity.Warning,
            NotificationLevel.Error => NotificationSeverity.Error,
            _ => NotificationSeverity.Info,
        };
        Snackbar.Show(notification.Title, notification.Message, tone);
    }
}
