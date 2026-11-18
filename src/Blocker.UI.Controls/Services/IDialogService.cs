namespace Blocker.UI.Controls.Services;

/// <summary>SKELETON: modal confirmation dialogs hosted inside the current window (never a message box).</summary>
public interface IDialogService
{
    /// <summary>Asks the user to confirm an action.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Body text.</param>
    /// <param name="primary">Text of the confirming button.</param>
    /// <param name="secondary">Text of the cancelling button, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns><see langword="true"/> when the user confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message, string primary, string? secondary, CancellationToken cancellationToken);
}
