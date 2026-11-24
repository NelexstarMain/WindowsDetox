using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>
/// Session setup page. Its data context is the <see cref="SetupViewModel"/>; the picker shows the <see cref="AllowlistViewModel"/> and the
/// saved-set row the <see cref="AppSetsViewModel"/>. Code-behind only wires those and handles the tile toggle gestures.
/// </summary>
internal sealed partial class SetupPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Session length, passes and summary.</param>
    /// <param name="allowlist">The app picker and domain list.</param>
    /// <param name="sets">The saved app sets.</param>
    public SetupPage(SetupViewModel viewModel, AllowlistViewModel allowlist, AppSetsViewModel sets)
    {
        DataContext = viewModel;
        InitializeComponent();
        PickerHost.DataContext = allowlist;
        SetsHost.DataContext = sets;
    }

    private static void Toggle(object sender)
    {
        if (sender is ListBoxItem { DataContext: AppEntryViewModel app })
        {
            app.IsSelected = !app.IsSelected;
        }
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e) => Toggle(sender);

    private void OnTileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Toggle(sender);
            e.Handled = true;
        }
    }

    private void OnSetNameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is UIElement box)
        {
            _ = Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
        }
    }
}
