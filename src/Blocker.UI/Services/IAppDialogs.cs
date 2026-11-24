using Blocker.UI.ViewModels;

namespace Blocker.UI.Services;

/// <summary>Application-specific modal dialogs hosted inside the main window. Confirmations use the shared <c>IDialogService</c>.</summary>
internal interface IAppDialogs
{
    /// <summary>
    /// Shows the one-time break-glass code with a copy button. Completes only after the user explicitly confirms
    /// that the code is saved; the dialog cannot be dismissed any other way.
    /// </summary>
    /// <param name="code">The recovery code. It is never stored or shown again.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the user confirmed.</returns>
    Task ShowBreakGlassCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Asks for the recovery code.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The entered code, or <see langword="null"/> when cancelled.</returns>
    Task<string?> PromptBreakGlassCodeAsync(CancellationToken cancellationToken);

    /// <summary>Shows the result of a plugin installation: stages, errors and per-test outcomes.</summary>
    /// <param name="report">The mapped report.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the dialog is closed.</returns>
    Task ShowInstallReportAsync(PluginInstallReport report, CancellationToken cancellationToken);
}

/// <summary>Native file pickers.</summary>
internal interface IFileDialogs
{
    /// <summary>Asks where to save a file.</summary>
    /// <param name="suggestedFileName">Initial file name.</param>
    /// <param name="filter">Win32 filter string, for example <c>ZIP archive|*.zip</c>.</param>
    /// <returns>The chosen path, or <see langword="null"/> when cancelled.</returns>
    string? PickSavePath(string suggestedFileName, string filter);

    /// <summary>Asks which file to open.</summary>
    /// <param name="filter">Win32 filter string.</param>
    /// <returns>The chosen path, or <see langword="null"/> when cancelled.</returns>
    string? PickOpenPath(string filter);
}

/// <summary>Pages that can be opened programmatically.</summary>
internal enum AppPage
{
    /// <summary>Session dashboard.</summary>
    Dashboard,

    /// <summary>The allowlist, now part of the session setup page.</summary>
    Allowlist,

    /// <summary>Session setup: allowlist, length and emergency passes.</summary>
    Setup,

    /// <summary>Per-app scheduled blocks and usage pools.</summary>
    AppLimits,

    /// <summary>Plugins.</summary>
    Plugins,

    /// <summary>Settings.</summary>
    Settings,
}

/// <summary>Lets view models ask the shell to show a page.</summary>
internal interface INavigator
{
    /// <summary>Raised when a page should be shown.</summary>
    event EventHandler<AppPage>? NavigationRequested;

    /// <summary>Raised when a plugin's own page should be shown. The argument is the plugin id.</summary>
    event EventHandler<string>? PluginPageRequested;

    /// <summary>Requests navigation.</summary>
    /// <param name="page">Target page.</param>
    void GoTo(AppPage page);

    /// <summary>Requests the first page of a plugin, or the Plugins page when it has none.</summary>
    /// <param name="pluginId">The plugin.</param>
    void GoToPluginPage(string pluginId);
}

/// <summary>Default <see cref="INavigator"/>; the shell subscribes to it.</summary>
internal sealed class Navigator : INavigator
{
    /// <inheritdoc />
    public event EventHandler<AppPage>? NavigationRequested;

    /// <inheritdoc />
    public event EventHandler<string>? PluginPageRequested;

    /// <inheritdoc />
    public void GoTo(AppPage page) => NavigationRequested?.Invoke(this, page);

    /// <inheritdoc />
    public void GoToPluginPage(string pluginId) => PluginPageRequested?.Invoke(this, pluginId);
}
