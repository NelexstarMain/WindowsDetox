using System.Windows.Controls;
using System.Windows.Input;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Views;

/// <summary>Allowlist page; behaviour lives in <see cref="AllowlistViewModel"/>, except the tile toggle gestures.</summary>
internal sealed partial class AllowlistPage : Page
{
    /// <summary>Creates the page.</summary>
    /// <param name="viewModel">Its view model.</param>
    public AllowlistPage(AllowlistViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
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
}
