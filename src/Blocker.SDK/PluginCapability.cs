using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Blocker.Sdk;

/// <summary>
/// Permissions a plugin requests in the <c>capabilities</c> array of <c>plugin.json</c>. The host denies any
/// API whose capability was not declared, both at compile time (static API analysis) and at runtime.
/// </summary>
public enum PluginCapability
{
    /// <summary><c>events.session</c>: observe session start/end and emergency pass events.</summary>
    SessionEvents,

    /// <summary><c>events.process</c>: observe <see cref="Events.ProcessBlocked"/>.</summary>
    ProcessEvents,

    /// <summary><c>events.window</c>: observe <see cref="Events.ActiveWindowChanged"/> (without titles).</summary>
    WindowEvents,

    /// <summary><c>events.window.title</c>: receive window titles. Privacy-sensitive; shown prominently on install.</summary>
    WindowTitles,

    /// <summary><c>events.idle</c>: observe <see cref="Events.UserIdleChanged"/>.</summary>
    IdleEvents,

    /// <summary><c>events.custom</c>: publish and subscribe to custom plugin events.</summary>
    CustomEvents,

    /// <summary><c>intercept.process</c>: register an <see cref="Extensions.IProcessInterceptor"/>.</summary>
    ProcessInterception,

    /// <summary><c>intercept.window</c>: register an <see cref="Extensions.IWindowInterceptor"/>.</summary>
    WindowInterception,

    /// <summary><c>tick</c>: register an <see cref="Extensions.ITickListener"/>.</summary>
    Tick,

    /// <summary><c>lockpolicy</c>: register an <see cref="Extensions.ILockPolicyContributor"/>.</summary>
    LockPolicy,

    /// <summary><c>ipc</c>: expose methods through <see cref="Ipc.IIpcRegistry"/>.</summary>
    Ipc,

    /// <summary><c>ui.page</c>: add a page to the side navigation.</summary>
    UiPage,

    /// <summary><c>ui.dashboard</c>: add a tile to the session dashboard.</summary>
    UiDashboardWidget,

    /// <summary><c>ui.settings</c>: add a section to the settings page.</summary>
    UiSettingsSection,

    /// <summary><c>ui.blockscreen</c>: add content to the block screen shown by <c>Blocker.Stub</c>.</summary>
    UiBlockScreen,

    /// <summary><c>network</c>: use outbound HTTP(S). Blocked during a session unless the host is on the web allowlist.</summary>
    Network,
}

/// <summary>
/// Maps <see cref="PluginCapability"/> values to and from their manifest names (for example <c>events.process</c>).
/// </summary>
public static class PluginCapabilities
{
    private static readonly FrozenDictionary<PluginCapability, string> ToName = new Dictionary<PluginCapability, string>
    {
        [PluginCapability.SessionEvents] = "events.session",
        [PluginCapability.ProcessEvents] = "events.process",
        [PluginCapability.WindowEvents] = "events.window",
        [PluginCapability.WindowTitles] = "events.window.title",
        [PluginCapability.IdleEvents] = "events.idle",
        [PluginCapability.CustomEvents] = "events.custom",
        [PluginCapability.ProcessInterception] = "intercept.process",
        [PluginCapability.WindowInterception] = "intercept.window",
        [PluginCapability.Tick] = "tick",
        [PluginCapability.LockPolicy] = "lockpolicy",
        [PluginCapability.Ipc] = "ipc",
        [PluginCapability.UiPage] = "ui.page",
        [PluginCapability.UiDashboardWidget] = "ui.dashboard",
        [PluginCapability.UiSettingsSection] = "ui.settings",
        [PluginCapability.UiBlockScreen] = "ui.blockscreen",
        [PluginCapability.Network] = "network",
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, PluginCapability> FromName =
        ToName.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>Gets all manifest names in declaration order.</summary>
    public static IReadOnlyList<string> AllNames { get; } =
        [.. Enum.GetValues<PluginCapability>().Select(c => ToName[c])];

    /// <summary>Returns the manifest name of <paramref name="capability"/>.</summary>
    /// <param name="capability">Capability to convert.</param>
    /// <returns>The manifest name, for example <c>events.process</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined capability.</exception>
    public static string ToManifestName(PluginCapability capability) =>
        ToName.TryGetValue(capability, out string? name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown capability.");

    /// <summary>
    /// Returns the capability required to subscribe to the core event type <paramref name="eventType"/>.
    /// </summary>
    /// <param name="eventType">A type implementing <see cref="Events.IBlockerEvent"/>.</param>
    /// <returns>The required capability.</returns>
    /// <exception cref="ArgumentException"><paramref name="eventType"/> is not a known core event type.</exception>
    public static PluginCapability RequiredForEvent(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return eventType switch
        {
            _ when eventType == typeof(Events.SessionStarted) => PluginCapability.SessionEvents,
            _ when eventType == typeof(Events.SessionEnded) => PluginCapability.SessionEvents,
            _ when eventType == typeof(Events.EmergencyPassStarted) => PluginCapability.SessionEvents,
            _ when eventType == typeof(Events.EmergencyPassEnded) => PluginCapability.SessionEvents,
            _ when eventType == typeof(Events.ProcessBlocked) => PluginCapability.ProcessEvents,
            _ when eventType == typeof(Events.ActiveWindowChanged) => PluginCapability.WindowEvents,
            _ when eventType == typeof(Events.UserIdleChanged) => PluginCapability.IdleEvents,
            _ when eventType == typeof(Events.CustomPluginEvent) => PluginCapability.CustomEvents,
            _ => throw new ArgumentException($"'{eventType.Name}' is not a core Blocker event type.", nameof(eventType)),
        };
    }

    /// <summary>Parses a manifest capability name. Matching is ordinal and case-sensitive.</summary>
    /// <param name="name">Manifest name.</param>
    /// <param name="capability">Parsed capability when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a known capability.</returns>
    public static bool TryParse([NotNullWhen(true)] string? name, out PluginCapability capability)
    {
        if (name is not null && FromName.TryGetValue(name, out capability))
        {
            return true;
        }

        capability = default;
        return false;
    }
}
