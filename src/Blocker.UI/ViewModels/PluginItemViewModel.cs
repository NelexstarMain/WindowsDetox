using Blocker.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blocker.UI.ViewModels;

/// <summary>Lifecycle state of an installed plugin.</summary>
internal enum PluginState
{
    /// <summary>Installed and running.</summary>
    Enabled,

    /// <summary>Installed but switched off.</summary>
    Disabled,

    /// <summary>Stopped by the circuit breaker after repeated failures.</summary>
    Faulted,
}

/// <summary>A capability chip on a plugin card.</summary>
/// <param name="Name">Manifest name, for example <c>events.window.title</c>.</param>
/// <param name="Label">Plain-language label.</param>
/// <param name="IsPrivacySensitive">Whether the capability exposes personal data and is shown with a warning.</param>
internal sealed record CapabilityChip(string Name, string Label, bool IsPrivacySensitive)
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["events.session"] = "Session events",
        ["events.process"] = "Blocked-app events",
        ["events.window"] = "Active window events",
        ["events.window.title"] = "Reads window titles",
        ["events.idle"] = "Idle events",
        ["events.custom"] = "Custom events",
        ["intercept.process"] = "Can tighten app blocking",
        ["intercept.window"] = "Can tighten window rules",
        ["tick"] = "Runs every second",
        ["lockpolicy"] = "Can extend locks",
        ["ipc"] = "Exposes methods",
        ["ui.page"] = "Adds a page",
        ["ui.dashboard"] = "Adds a dashboard widget",
        ["ui.settings"] = "Adds a settings section",
        ["ui.blockscreen"] = "Adds to the block screen",
        ["network"] = "Uses the network",
    };

    /// <summary>The manifest name of the privacy-sensitive window-title capability.</summary>
    public const string WindowTitles = "events.window.title";

    /// <summary>Creates a chip for a manifest capability name.</summary>
    /// <param name="name">Manifest name.</param>
    /// <returns>The chip.</returns>
    public static CapabilityChip From(string name) =>
        new(name, Labels.TryGetValue(name, out string? label) ? label : name, string.Equals(name, WindowTitles, StringComparison.Ordinal));
}

/// <summary>An installed plugin as shown in the plugin list.</summary>
internal sealed partial class PluginItemViewModel : ObservableObject
{
    [ObservableProperty]
    private PluginState _state;

    /// <summary>Creates the item from its wire form.</summary>
    /// <param name="dto">Plugin description.</param>
    public PluginItemViewModel(PluginInfoDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Id = dto.Id;
        Name = dto.Name;
        Version = dto.Version;
        Author = dto.Author;
        Capabilities = [.. dto.Capabilities.Select(CapabilityChip.From).OrderByDescending(c => c.IsPrivacySensitive).ThenBy(c => c.Label, StringComparer.CurrentCulture)];
        State = dto.Faulted ? PluginState.Faulted : dto.Enabled ? PluginState.Enabled : PluginState.Disabled;
    }

    /// <summary>Gets the plugin id.</summary>
    public string Id { get; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; }

    /// <summary>Gets the version text.</summary>
    public string Version { get; }

    /// <summary>Gets the author.</summary>
    public string Author { get; }

    /// <summary>Gets the capability chips, privacy-sensitive first.</summary>
    public IReadOnlyList<CapabilityChip> Capabilities { get; }

    /// <summary>Gets whether the plugin can read window titles.</summary>
    public bool HasPrivacySensitiveCapability => Capabilities.Any(c => c.IsPrivacySensitive);

    /// <summary>Gets the state as text for the state badge.</summary>
    public string StateLabel => State switch
    {
        PluginState.Enabled => "Enabled",
        PluginState.Faulted => "Faulted",
        _ => "Disabled",
    };

    /// <summary>Gets the text of the enable/disable button.</summary>
    public string ToggleLabel => State == PluginState.Enabled ? "Disable" : "Enable";

    /// <summary>Gets whether the plugin is currently enabled.</summary>
    public bool IsEnabled => State == PluginState.Enabled;

    partial void OnStateChanged(PluginState value)
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(IsEnabled));
    }
}
