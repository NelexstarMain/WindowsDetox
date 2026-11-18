using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Blocker.UI.Views;

/// <summary>Main window: hosts the navigation rail, dialogs and snackbars. All behaviour lives in view models and services.</summary>
internal sealed partial class MainWindow : FluentWindow
{
    private readonly NavigationBinder _binder;

    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">Shell view model.</param>
    /// <param name="dialogs">Dialog service; its host becomes this window.</param>
    /// <param name="snackbars">Snackbar service; its presenter becomes this window.</param>
    /// <param name="navigator">Navigation requests from view models.</param>
    /// <param name="pageProvider">Creates pages through DI.</param>
    public MainWindow(
        ShellViewModel viewModel,
        IContentDialogService dialogs,
        Wpf.Ui.ISnackbarService snackbars,
        INavigator navigator,
        INavigationViewPageProvider pageProvider)
    {
        DataContext = viewModel;
        InitializeComponent();
        dialogs.SetDialogHost(RootContentDialog);
        snackbars.SetSnackbarPresenter(RootSnackbar);
        _binder = new NavigationBinder(RootNavigation, viewModel, navigator, pageProvider);
    }

    /// <summary>Gets the binder that wires the rail; kept alive for the window's lifetime.</summary>
    internal NavigationBinder Binder => _binder;
}
