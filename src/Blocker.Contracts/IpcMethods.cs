using System.Diagnostics.CodeAnalysis;
using Blocker.Sdk;

namespace Blocker.Contracts;

/// <summary>Names of all IPC methods and server-pushed notifications. Names are part of the wire contract and never change.</summary>
public static class IpcMethods
{
    /// <summary>Prefix of plugin-provided methods: <c>plugin.{pluginId}.{method}</c>.</summary>
    public const string PluginPrefix = "plugin.";

    /// <summary>Returns the current <see cref="SessionStatusDto"/>.</summary>
    public const string SessionGetStatus = "session.getStatus";

    /// <summary>Starts a session (<see cref="StartSessionRequest"/>); rejected while a session runs.</summary>
    public const string SessionStart = "session.start";

    /// <summary>Starts an emergency pass.</summary>
    public const string SessionUseEmergencyPass = "session.useEmergencyPass";

    /// <summary>Ends the session with the one-time recovery code (<see cref="BreakGlassRequest"/>).</summary>
    public const string SessionBreakGlass = "session.breakGlass";

    /// <summary>Developer abort; only honoured by DEVMODE service builds.</summary>
    public const string SessionDevAbort = "session.devAbort";

    /// <summary>Scans installed applications for the allowlist picker.</summary>
    public const string AllowlistScanApps = "allowlist.scanApps";

    /// <summary>Returns the allowlist.</summary>
    public const string AllowlistGet = "allowlist.get";

    /// <summary>Replaces the allowlist; rejected while a session runs.</summary>
    public const string AllowlistSet = "allowlist.set";

    /// <summary>Returns the settings.</summary>
    public const string SettingsGet = "settings.get";

    /// <summary>Replaces settings values; rejected while a session runs.</summary>
    public const string SettingsSet = "settings.set";

    /// <summary>Lists installed plugins.</summary>
    public const string PluginsList = "plugins.list";

    /// <summary>Installs a plugin ZIP (base64); administrators only, rejected while a session runs.</summary>
    public const string PluginsInstall = "plugins.install";

    /// <summary>Removes a plugin; administrators only, rejected while a session runs.</summary>
    public const string PluginsRemove = "plugins.remove";

    /// <summary>Enables or disables a plugin; administrators only, rejected while a session runs.</summary>
    public const string PluginsSetEnabled = "plugins.setEnabled";

    /// <summary>Returns a starter plugin template as a ZIP (base64).</summary>
    public const string PluginsDownloadTemplate = "plugins.downloadTemplate";

    /// <summary>Returns the serialized UI trees of one slot.</summary>
    public const string UiGetContributions = "ui.getContributions";

    /// <summary>Forwards a user interaction to a plugin contribution.</summary>
    public const string UiSendAction = "ui.sendAction";

    /// <summary>User-session agent hello.</summary>
    public const string AgentRegister = "agent.register";

    /// <summary>Agent reports the foreground window (notification).</summary>
    public const string AgentForegroundWindow = "agent.foregroundWindow";

    /// <summary>Agent reports idle/active transitions (notification).</summary>
    public const string AgentIdleChanged = "agent.idleChanged";

    /// <summary>Service asks the agent to close a process gracefully. Service to agent: no client may call it on the server.</summary>
    public const string AgentCloseProcess = "agent.closeProcess";

    /// <summary>Service asks the agent to show a toast. Service to agent: no client may call it on the server.</summary>
    public const string AgentNotify = "agent.notify";

    /// <summary>Block screen asks why it was shown.</summary>
    public const string StubGetBlockInfo = "stub.getBlockInfo";

    /// <summary>Plugin host hello on its dedicated low-integrity pipe.</summary>
    public const string PluginHostRegister = "pluginHost.register";

    /// <summary>Subscribes the connection to server pushes (<see cref="EventSessionChanged"/>, <see cref="EventNotification"/>).</summary>
    public const string EventsSubscribe = "events.subscribe";

    /// <summary>Server push: a <see cref="SessionStatusDto"/> after every state change.</summary>
    public const string EventSessionChanged = "events.sessionChanged";

    /// <summary>Server push: a <see cref="NotificationDto"/>.</summary>
    public const string EventNotification = "events.notification";

    /// <summary>Longest accepted method name.</summary>
    public const int MaxMethodLength = 192;

    /// <summary>Builds <c>plugin.{pluginId}.{method}</c>.</summary>
    /// <param name="pluginId">Valid plugin id.</param>
    /// <param name="method">Method name as registered by the plugin.</param>
    /// <returns>The wire method name.</returns>
    public static string ForPlugin(string pluginId, string method) => $"{PluginPrefix}{pluginId}.{method}";

    /// <summary>Splits a wire method name of the form <c>plugin.{pluginId}.{method}</c>.</summary>
    /// <param name="wireMethod">Wire method name.</param>
    /// <param name="pluginId">Plugin id on success.</param>
    /// <param name="method">Plugin-local method on success.</param>
    /// <returns><see langword="true"/> when the name is a well-formed plugin method.</returns>
    public static bool TryParsePluginMethod(string? wireMethod, [NotNullWhen(true)] out string? pluginId, [NotNullWhen(true)] out string? method)
    {
        pluginId = null;
        method = null;
        if (wireMethod is not { Length: > 0 and <= MaxMethodLength } || !wireMethod.StartsWith(PluginPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Plugin ids contain dots but plugin methods never do, so the method is everything after the last dot.
        var rest = wireMethod.AsSpan(PluginPrefix.Length);
        var lastDot = rest.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return false;
        }

        var candidateId = rest[..lastDot].ToString();
        var candidateMethod = rest[(lastDot + 1)..].ToString();
        if (!PluginId.IsValid(candidateId) || !IsValidPluginMethod(candidateMethod))
        {
            return false;
        }

        pluginId = candidateId;
        method = candidateMethod;
        return true;
    }

    private static bool IsValidPluginMethod(string method)
    {
        if (method is not { Length: > 0 and <= 64 } || !char.IsAsciiLetterLower(method[0]))
        {
            return false;
        }

        foreach (var c in method)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
