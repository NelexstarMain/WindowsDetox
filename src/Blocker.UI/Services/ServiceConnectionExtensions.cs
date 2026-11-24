using Blocker.Contracts;
using Blocker.UI.Sdk;

namespace Blocker.UI.Services;

/// <summary>Typed wrappers over <see cref="IServiceConnection.InvokeAsync{TResponse}"/> so call sites name the contract method once.</summary>
internal static class ServiceConnectionExtensions
{
    /// <summary>Starts a session.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="request">Session configuration and allowlist.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome including the one-time break-glass code.</returns>
    public static ValueTask<StartSessionResponse> StartSessionAsync(this IServiceConnection connection, StartSessionRequest request, CancellationToken cancellationToken) =>
        connection.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, request, cancellationToken);

    /// <summary>Starts an emergency pass.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome and the new status.</returns>
    public static ValueTask<EmergencyPassResponse> UseEmergencyPassAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, cancellationToken);

    /// <summary>Ends the session with the recovery code.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="code">Recovery code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether the session ended.</returns>
    public static ValueTask<BreakGlassResponse> BreakGlassAsync(this IServiceConnection connection, string code, CancellationToken cancellationToken) =>
        connection.InvokeAsync<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, new BreakGlassRequest(code), cancellationToken);

    /// <summary>Scans installed applications.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The candidates.</returns>
    public static ValueTask<ScanAppsResponse> ScanAppsAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<ScanAppsResponse>(IpcMethods.AllowlistScanApps, cancellationToken);

    /// <summary>Reads the stored allowlist.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The allowlist.</returns>
    public static ValueTask<AllowlistDto> GetAllowlistAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AllowlistDto>(IpcMethods.AllowlistGet, cancellationToken);

    /// <summary>Replaces the stored allowlist.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="allowlist">New allowlist.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<OperationResponse> SetAllowlistAsync(this IServiceConnection connection, AllowlistDto allowlist, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, allowlist, cancellationToken);

    /// <summary>Reads the saved app sets.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The sets.</returns>
    public static ValueTask<AppSetsDto> GetAppSetsAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AppSetsDto>(IpcMethods.AppSetsGet, cancellationToken);

    /// <summary>Replaces the saved app sets.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="sets">New sets.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<OperationResponse> SetAppSetsAsync(this IServiceConnection connection, AppSetsDto sets, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AppSetsDto, OperationResponse>(IpcMethods.AppSetsSet, sets, cancellationToken);

    /// <summary>Reads the per-app rules.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The rules.</returns>
    public static ValueTask<AppRulesDto> GetAppRulesAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AppRulesDto>(IpcMethods.AppRulesGet, cancellationToken);

    /// <summary>Replaces the per-app rules.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="rules">New rules.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome; the service rejects loosening a rule while its app is blocked.</returns>
    public static ValueTask<OperationResponse> SetAppRulesAsync(this IServiceConnection connection, AppRulesDto rules, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AppRulesDto, OperationResponse>(IpcMethods.AppRulesSet, rules, cancellationToken);

    /// <summary>Reads the live state of every per-app rule.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One status per rule.</returns>
    public static ValueTask<AppRulesStatusResponse> GetAppRulesStatusAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<AppRulesStatusResponse>(IpcMethods.AppRulesStatus, cancellationToken);

    /// <summary>Reads the settings.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The settings.</returns>
    public static ValueTask<SettingsDto> GetSettingsAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<SettingsDto>(IpcMethods.SettingsGet, cancellationToken);

    /// <summary>Lists installed plugins.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The plugins.</returns>
    public static ValueTask<ListPluginsResponse> ListPluginsAsync(this IServiceConnection connection, CancellationToken cancellationToken) =>
        connection.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, cancellationToken);

    /// <summary>Installs a plugin package.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="packageBase64">ZIP bytes, base64 encoded.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<InstallPluginResponse> InstallPluginAsync(this IServiceConnection connection, string packageBase64, CancellationToken cancellationToken) =>
        connection.InvokeAsync<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, new InstallPluginRequest(packageBase64), cancellationToken);

    /// <summary>Removes a plugin.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="pluginId">Plugin id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<OperationResponse> RemovePluginAsync(this IServiceConnection connection, string pluginId, CancellationToken cancellationToken) =>
        connection.InvokeAsync<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, new RemovePluginRequest(pluginId), cancellationToken);

    /// <summary>Enables or disables a plugin.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="pluginId">Plugin id.</param>
    /// <param name="enabled">New state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<OperationResponse> SetPluginEnabledAsync(this IServiceConnection connection, string pluginId, bool enabled, CancellationToken cancellationToken) =>
        connection.InvokeAsync<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, new SetPluginEnabledRequest(pluginId, enabled), cancellationToken);

    /// <summary>Fetches the serialized UI trees of one slot.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="slot">Slot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The contributions.</returns>
    public static ValueTask<GetContributionsResponse> GetContributionsAsync(this IServiceConnection connection, UiSlot slot, CancellationToken cancellationToken) =>
        connection.InvokeAsync<GetContributionsRequest, GetContributionsResponse>(IpcMethods.UiGetContributions, new GetContributionsRequest(slot), cancellationToken);

    /// <summary>Forwards a user interaction to a plugin contribution.</summary>
    /// <param name="connection">Connection.</param>
    /// <param name="pluginId">Owning plugin.</param>
    /// <param name="contributionId">Contribution id.</param>
    /// <param name="action">The interaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>An empty response.</returns>
    public static ValueTask<EmptyDto> SendActionAsync(this IServiceConnection connection, string pluginId, string contributionId, UiAction action, CancellationToken cancellationToken) =>
        connection.InvokeAsync<SendActionRequest, EmptyDto>(IpcMethods.UiSendAction, new SendActionRequest(pluginId, contributionId, action), cancellationToken);
}
