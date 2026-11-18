namespace Blocker.UI.Controls.Services;

/// <summary>SKELETON: tone of a snackbar.</summary>
public enum SnackbarTone
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>Operation succeeded.</summary>
    Success,

    /// <summary>Needs attention.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Danger,
}

/// <summary>SKELETON: non-blocking notifications shown over the current window.</summary>
public interface ISnackbarService
{
    /// <summary>Shows a snackbar.</summary>
    /// <param name="title">Short title.</param>
    /// <param name="message">Body text.</param>
    /// <param name="tone">Visual tone.</param>
    void Show(string title, string message, SnackbarTone tone);
}
