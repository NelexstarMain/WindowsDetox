using Blocker.UI.ViewModels;
using Blocker.UI.Views.Dialogs;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Blocker.UI.Services;

/// <summary>Application dialogs hosted by the main window through WPF-UI's content dialog service.</summary>
/// <param name="dialogs">WPF-UI dialog service whose host is the main window.</param>
internal sealed class AppDialogs(IContentDialogService dialogs) : IAppDialogs
{
    /// <inheritdoc />
    public async Task ShowBreakGlassCodeAsync(string code, CancellationToken cancellationToken)
    {
        ContentDialogResult result;
        do
        {
            // No close button: the dialog returns only when the user confirms, and reopens if it was dismissed another way.
            ContentDialog dialog = new()
            {
                Title = "Save your break-glass code",
                Content = new BreakGlassCodeContent { DataContext = new BreakGlassCodeDialogViewModel(code) },
                PrimaryButtonText = "I have saved it",
                DefaultButton = ContentDialogButton.None,
            };
            result = await dialogs.ShowAsync(dialog, cancellationToken);
        }
        while (result != ContentDialogResult.Primary);
    }

    /// <inheritdoc />
    public async Task<string?> PromptBreakGlassCodeAsync(CancellationToken cancellationToken)
    {
        BreakGlassPromptDialogViewModel model = new();
        ContentDialog dialog = new()
        {
            Title = "Enter break-glass code",
            Content = new BreakGlassPromptContent { DataContext = model },
            PrimaryButtonText = "End session",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result = await dialogs.ShowAsync(dialog, cancellationToken);
        return result == ContentDialogResult.Primary ? model.Code : null;
    }

    /// <inheritdoc />
    public async Task ShowInstallReportAsync(PluginInstallReport report, CancellationToken cancellationToken)
    {
        ContentDialog dialog = new()
        {
            Title = report.Succeeded ? "Plugin installed" : "Plugin not installed",
            Content = new InstallReportContent { DataContext = report },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        _ = await dialogs.ShowAsync(dialog, cancellationToken);
    }
}
