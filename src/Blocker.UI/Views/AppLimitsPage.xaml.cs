using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Blocker.UI.Sdk;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>App limits page: wires the view model to the list grouping, the week grid and a status refresh timer that runs while the page is shown.</summary>
internal sealed partial class AppLimitsPage : Page
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(15);

    private readonly AppLimitsViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;

    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public AppLimitsPage(AppLimitsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ICollectionView view = CollectionViewSource.GetDefaultView(viewModel.Items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppLimitItemViewModel.GroupName)));
        _statusTimer = new DispatcherTimer(StatusInterval, DispatcherPriority.Background, OnStatusTick, Dispatcher);
        _statusTimer.Stop();
        Loaded += (_, _) =>
        {
            _ = viewModel.RefreshAsync(CancellationToken.None);
            _statusTimer.Start();
        };
        Unloaded += (_, _) => _statusTimer.Stop();
    }

    private void OnStatusTick(object? sender, EventArgs e) => _ = _viewModel.PollStatusAsync();

    private void OnGridAction(object? sender, UiAction action) => _viewModel.HandleGridAction(action);
}
