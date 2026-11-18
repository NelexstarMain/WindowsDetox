using System.Windows;
using System.Windows.Controls;
using Blocker.UI.Controls.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Blocker.UI.Services;

/// <summary>Confirmation dialogs hosted by the main window through WPF-UI's content dialog service.</summary>
/// <param name="dialogs">WPF-UI dialog service whose host is the main window.</param>
internal sealed class FluentDialogService(IContentDialogService dialogs) : Blocker.UI.Controls.Services.IDialogService
{
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string primary, string? secondary, CancellationToken cancellationToken)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = secondary ?? "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result = await dialogs.ShowAsync(dialog, cancellationToken);
        return result == ContentDialogResult.Primary;
    }
}

/// <summary>Snackbars shown by the main window through WPF-UI's snackbar service.</summary>
/// <param name="snackbars">WPF-UI snackbar service whose presenter is the main window.</param>
internal sealed class FluentSnackbarService(Wpf.Ui.ISnackbarService snackbars) : Blocker.UI.Controls.Services.ISnackbarService
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    /// <inheritdoc />
    public void Show(string title, string message, SnackbarTone tone)
    {
        (ControlAppearance appearance, SymbolRegular symbol) = tone switch
        {
            SnackbarTone.Success => (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24),
            SnackbarTone.Warning => (ControlAppearance.Caution, SymbolRegular.Warning24),
            SnackbarTone.Danger => (ControlAppearance.Danger, SymbolRegular.ErrorCircle24),
            _ => (ControlAppearance.Secondary, SymbolRegular.Info24),
        };
        snackbars.Show(title, message, appearance, new SymbolIcon(symbol), Duration);
    }
}
