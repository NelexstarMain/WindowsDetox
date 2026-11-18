using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Blocker.UI.Controls.Services;

/// <summary>Severity of a snackbar notification.</summary>
public enum NotificationSeverity
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>A completed action.</summary>
    Success,

    /// <summary>Something needs attention.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>In-app dialogs. Apps use this instead of <c>MessageBox</c>.</summary>
public interface IDialogService
{
    /// <summary>Sets the host that shows dialogs, usually a <c>ContentDialogHost</c> at the root of the main window.</summary>
    /// <param name="host">The dialog host.</param>
    void SetHost(ContentDialogHost host);

    /// <summary>Shows a message with a single close button.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message text.</param>
    /// <param name="closeText">Close button text.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    Task ShowMessageAsync(string title, string message, string closeText = "Close", CancellationToken cancellationToken = default);

    /// <summary>Asks for confirmation.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message text.</param>
    /// <param name="confirmText">Confirm button text; name the action, for example "Start session".</param>
    /// <param name="cancelText">Cancel button text.</param>
    /// <param name="cancellationToken">Cancels the dialog.</param>
    /// <returns><see langword="true"/> when the user confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel", CancellationToken cancellationToken = default);
}

/// <summary>Transient notifications.</summary>
public interface ISnackbarService
{
    /// <summary>Sets the presenter that hosts snackbars, usually at the bottom of the main window.</summary>
    /// <param name="presenter">The presenter.</param>
    void SetPresenter(SnackbarPresenter presenter);

    /// <summary>Shows a notification.</summary>
    /// <param name="title">Title.</param>
    /// <param name="message">Message text.</param>
    /// <param name="severity">Severity.</param>
    /// <param name="timeout">Display time; <see langword="null"/> uses four seconds.</param>
    void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? timeout = null);
}

/// <summary>Thin wrapper over the WPF-UI content dialog service.</summary>
public sealed class BlockerDialogService : IDialogService
{
    private readonly IContentDialogService _inner;

    /// <summary>Initializes a new instance of the <see cref="BlockerDialogService"/> class.</summary>
    /// <param name="inner">The WPF-UI service to wrap; a new one when <see langword="null"/>.</param>
    public BlockerDialogService(IContentDialogService? inner = null) => _inner = inner ?? new ContentDialogService();

    /// <inheritdoc />
    public void SetHost(ContentDialogHost host) => _inner.SetDialogHost(host);

    /// <inheritdoc />
    public async Task ShowMessageAsync(string title, string message, string closeText = "Close", CancellationToken cancellationToken = default) =>
        _ = await _inner.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions { Title = title, Content = message, CloseButtonText = closeText },
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText = "Cancel", CancellationToken cancellationToken = default)
    {
        ContentDialogResult result = await _inner.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions { Title = title, Content = message, PrimaryButtonText = confirmText, CloseButtonText = cancelText },
            cancellationToken);
        return result == ContentDialogResult.Primary;
    }
}

/// <summary>Thin wrapper over the WPF-UI snackbar service.</summary>
public sealed class BlockerSnackbarService : ISnackbarService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private readonly Wpf.Ui.ISnackbarService _inner;

    /// <summary>Initializes a new instance of the <see cref="BlockerSnackbarService"/> class.</summary>
    /// <param name="inner">The WPF-UI service to wrap; a new one when <see langword="null"/>.</param>
    public BlockerSnackbarService(Wpf.Ui.ISnackbarService? inner = null) => _inner = inner ?? new SnackbarService();

    /// <inheritdoc />
    public void SetPresenter(SnackbarPresenter presenter) => _inner.SetSnackbarPresenter(presenter);

    /// <inheritdoc />
    public void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? timeout = null)
    {
        (ControlAppearance appearance, SymbolRegular symbol) = severity switch
        {
            NotificationSeverity.Success => (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24),
            NotificationSeverity.Warning => (ControlAppearance.Caution, SymbolRegular.Warning24),
            NotificationSeverity.Error => (ControlAppearance.Danger, SymbolRegular.ErrorCircle24),
            _ => (ControlAppearance.Info, SymbolRegular.Info24),
        };

        _inner.Show(title, message, appearance, new SymbolIcon { Symbol = symbol }, timeout ?? DefaultTimeout);
    }
}
