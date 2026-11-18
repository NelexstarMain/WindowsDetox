using System.Windows;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Agent;

/// <summary>A small always-on-top toast; bound to a <see cref="ToastViewModel"/>.</summary>
internal sealed partial class ToastWindow : Window
{
    /// <summary>Creates the toast.</summary>
    /// <param name="viewModel">Its content and countdown.</param>
    public ToastWindow(ToastViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Moves the toast to the bottom-right corner of the primary work area.</summary>
    public void PlaceBottomRight()
    {
        Rect area = SystemParameters.WorkArea;
        UpdateLayout();
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }
}
