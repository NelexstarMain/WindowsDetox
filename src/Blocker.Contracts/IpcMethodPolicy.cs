using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Blocker.Contracts;

/// <summary>
/// Roles of an IPC caller, decided by the server from the connected process (never claimed by the client).
/// A caller can hold several roles at once, for example the UI executable is <see cref="User"/> and <see cref="Agent"/>,
/// and an elevated user is <see cref="User"/> and <see cref="Administrator"/>.
/// </summary>
[Flags]
public enum IpcCallerRole
{
    /// <summary>No role; such callers are refused.</summary>
    None = 0,

    /// <summary>Authenticated interactive user (any process).</summary>
    User = 1,

    /// <summary>Elevated administrator.</summary>
    Administrator = 2,

    /// <summary>The Blocker user-session agent (image verified to be inside the install directory).</summary>
    Agent = 4,

    /// <summary>The Blocker block screen (image verified).</summary>
    Stub = 8,

    /// <summary>The Blocker plugin host on its dedicated pipe (image verified).</summary>
    PluginHost = 16,
}

/// <summary>Transport limits shared by client and server.</summary>
public static class IpcLimits
{
    /// <summary>Largest message of any kind (<c>plugins.install</c>), in bytes.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>Default largest request for every other method, in bytes.</summary>
    public const int DefaultMaxPayloadBytes = 256 * 1024;
}

/// <summary>Access rule of one method.</summary>
/// <param name="Method">Method name, or the <c>plugin.*</c> pattern.</param>
/// <param name="AllowedRoles">Roles that may call the method on the server; <see cref="IpcCallerRole.None"/> for server-to-client methods.</param>
/// <param name="MaxPayloadBytes">Largest accepted request frame (the whole JSON message), in bytes.</param>
/// <param name="AllowedDuringSession">Whether the method may be called while a session runs.</param>
public sealed record IpcMethodRule(string Method, IpcCallerRole AllowedRoles, int MaxPayloadBytes, bool AllowedDuringSession)
{
    /// <summary>Returns whether a caller holding <paramref name="role"/> may call the method.</summary>
    /// <param name="role">Roles of the caller.</param>
    /// <returns><see langword="true"/> when at least one role is allowed.</returns>
    public bool Allows(IpcCallerRole role) => (AllowedRoles & role) != IpcCallerRole.None;
}

/// <summary>The method table: which roles may call what, how large a request may be and whether it works during a session.</summary>
public static class IpcMethodPolicy
{
    private const IpcCallerRole Human = IpcCallerRole.User | IpcCallerRole.Administrator;
    private const IpcCallerRole Ui = Human | IpcCallerRole.Stub;

    private static readonly FrozenDictionary<string, IpcMethodRule> Rules = Build();

    /// <summary>Rule that applies to every well-formed <c>plugin.{id}.{method}</c> name.</summary>
    public static IpcMethodRule PluginRule { get; } = new(IpcMethods.PluginPrefix + "*", Ui, IpcLimits.DefaultMaxPayloadBytes, true);

    /// <summary>Gets all fixed rules.</summary>
    public static IReadOnlyCollection<IpcMethodRule> All => Rules.Values;

    /// <summary>Looks up the rule of <paramref name="method"/>; plugin methods share <see cref="PluginRule"/>.</summary>
    /// <param name="method">Wire method name.</param>
    /// <param name="rule">The rule on success.</param>
    /// <returns><see langword="true"/> when the method exists in the contract.</returns>
    public static bool TryGet(string? method, [NotNullWhen(true)] out IpcMethodRule? rule)
    {
        if (method is not null && Rules.TryGetValue(method, out rule))
        {
            return true;
        }

        if (IpcMethods.TryParsePluginMethod(method, out _, out _))
        {
            rule = PluginRule;
            return true;
        }

        rule = null;
        return false;
    }

    /// <summary>Largest request frame any method allows for a caller with <paramref name="role"/>. Used to bound reads before parsing.</summary>
    /// <param name="role">Roles of the caller.</param>
    /// <returns>Limit in bytes.</returns>
    public static int MaxFrameBytesFor(IpcCallerRole role)
    {
        var max = PluginRule.Allows(role) ? PluginRule.MaxPayloadBytes : 0;
        foreach (var rule in Rules.Values)
        {
            if (rule.Allows(role) && rule.MaxPayloadBytes > max)
            {
                max = rule.MaxPayloadBytes;
            }
        }

        return max == 0 ? IpcLimits.DefaultMaxPayloadBytes : max;
    }

    private static FrozenDictionary<string, IpcMethodRule> Build()
    {
        const int normal = IpcLimits.DefaultMaxPayloadBytes;
        const IpcCallerRole all = Human | IpcCallerRole.Agent | IpcCallerRole.Stub;
        const IpcCallerRole serverToClient = IpcCallerRole.None;
        const IpcCallerRole admin = IpcCallerRole.Administrator;
        IpcMethodRule[] rules =
        [
            new(IpcMethods.SessionGetStatus, all, normal, true),
            new(IpcMethods.SessionStart, Human, normal, false),
            new(IpcMethods.SessionUseEmergencyPass, Ui, normal, true),
            new(IpcMethods.SessionBreakGlass, Human, normal, true),
            new(IpcMethods.SessionDevAbort, Human, normal, true),
            new(IpcMethods.AllowlistScanApps, Human, normal, true),
            new(IpcMethods.AllowlistGet, Human, normal, true),
            new(IpcMethods.AllowlistSet, Human, normal, false),
            new(IpcMethods.SettingsGet, Human, normal, true),
            new(IpcMethods.SettingsSet, Human, normal, false),
            new(IpcMethods.PluginsList, Ui, normal, true),
            new(IpcMethods.PluginsInstall, admin, IpcLimits.MaxMessageBytes, false),
            new(IpcMethods.PluginsRemove, admin, normal, false),
            new(IpcMethods.PluginsSetEnabled, admin, normal, false),
            new(IpcMethods.PluginsDownloadTemplate, Human, normal, true),
            new(IpcMethods.UiGetContributions, Ui, normal, true),
            new(IpcMethods.UiSendAction, Ui, normal, true),
            new(IpcMethods.AgentRegister, IpcCallerRole.Agent, normal, true),
            new(IpcMethods.AgentForegroundWindow, IpcCallerRole.Agent, normal, true),
            new(IpcMethods.AgentIdleChanged, IpcCallerRole.Agent, normal, true),
            new(IpcMethods.AgentCloseProcess, serverToClient, normal, true),
            new(IpcMethods.AgentNotify, serverToClient, normal, true),
            new(IpcMethods.StubGetBlockInfo, IpcCallerRole.Stub, normal, true),
            new(IpcMethods.PluginHostRegister, IpcCallerRole.PluginHost, normal, true),
            new(IpcMethods.PluginHostPluginFaulted, IpcCallerRole.PluginHost, normal, true),
            new(IpcMethods.PluginHostRenderRequested, IpcCallerRole.PluginHost, normal, true),
            new(IpcMethods.HostLoadPlugins, serverToClient, normal, true),
            new(IpcMethods.HostEvaluatePackage, serverToClient, IpcLimits.MaxMessageBytes, true),
            new(IpcMethods.HostInterceptProcess, serverToClient, normal, true),
            new(IpcMethods.HostInterceptWindow, serverToClient, normal, true),
            new(IpcMethods.HostEvaluateLock, serverToClient, normal, true),
            new(IpcMethods.HostRenderSlot, serverToClient, normal, true),
            new(IpcMethods.HostSendUiAction, serverToClient, normal, true),
            new(IpcMethods.HostInvokePluginMethod, serverToClient, normal, true),
            new(IpcMethods.HostSessionChanged, serverToClient, normal, true),
            new(IpcMethods.HostPublishEvent, serverToClient, normal, true),
            new(IpcMethods.HostShutdown, serverToClient, normal, true),
            new(IpcMethods.EventsSubscribe, all, normal, true),
            new(IpcMethods.EventSessionChanged, serverToClient, normal, true),
            new(IpcMethods.EventNotification, serverToClient, normal, true),
        ];
        return rules.ToFrozenDictionary(r => r.Method, StringComparer.Ordinal);
    }
}
