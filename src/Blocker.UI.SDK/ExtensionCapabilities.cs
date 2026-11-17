using Blocker.Sdk;
using Blocker.Sdk.Extensions;

namespace Blocker.UI.Sdk;

/// <summary>
/// Single source of truth for the capabilities required by each extension interface, backend and UI alike.
/// Used by the plugin host and by the testing kit so tests enforce exactly what production enforces.
/// </summary>
public static class ExtensionCapabilities
{
    /// <summary>Returns every capability required to register <paramref name="extension"/>.</summary>
    /// <param name="extension">Extension object; may implement several extension interfaces.</param>
    /// <returns>Required capabilities, without duplicates. Empty when no known interface is implemented.</returns>
    public static IReadOnlyList<PluginCapability> GetRequired(IPluginExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        List<PluginCapability> required = [];
        if (extension is IProcessInterceptor)
        {
            required.Add(PluginCapability.ProcessInterception);
        }

        if (extension is IWindowInterceptor)
        {
            required.Add(PluginCapability.WindowInterception);
        }

        if (extension is ITickListener)
        {
            required.Add(PluginCapability.Tick);
        }

        if (extension is ILockPolicyContributor)
        {
            required.Add(PluginCapability.LockPolicy);
        }

        if (extension is IUiPagePlugin)
        {
            required.Add(PluginCapability.UiPage);
        }

        if (extension is IDashboardWidgetPlugin)
        {
            required.Add(PluginCapability.UiDashboardWidget);
        }

        if (extension is ISettingsSectionPlugin)
        {
            required.Add(PluginCapability.UiSettingsSection);
        }

        if (extension is IBlockScreenPlugin)
        {
            required.Add(PluginCapability.UiBlockScreen);
        }

        return required;
    }
}
