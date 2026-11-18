using Blocker.Sdk.Session;
using Blocker.UI.ViewModels;

namespace Blocker.UI.Agent;

/// <summary>What a tray menu entry does.</summary>
internal enum TrayCommand
{
    /// <summary>Launch or activate the main window.</summary>
    Open,

    /// <summary>Stop the agent.</summary>
    Exit,
}

/// <summary>One entry of the tray menu.</summary>
/// <param name="Command">Action of the entry.</param>
/// <param name="Text">Menu text.</param>
internal sealed record TrayMenuItem(TrayCommand Command, string Text);

/// <summary>Pure presentation rules of the agent's tray icon: tooltip text and which menu entries exist.</summary>
internal static class TrayModel
{
    /// <summary>Longest tooltip the shell shows (<c>szTip</c> holds 128 characters including the terminator).</summary>
    public const int MaxTooltipLength = 127;

    private const string Name = "WindowsDetox";

    /// <summary>Builds the tooltip from the live countdown.</summary>
    /// <param name="timeline">Latest snapshot with its local countdown.</param>
    /// <param name="connected">Whether the service connection is up.</param>
    /// <returns>Tooltip text of at most <see cref="MaxTooltipLength"/> characters.</returns>
    public static string Tooltip(SessionTimeline timeline, bool connected)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        string text = (connected, timeline.Snapshot.Phase) switch
        {
            (false, _) => $"{Name} - service not connected",
            (_, SessionPhase.Active) => $"{Name} - {DurationFormat.Clock(timeline.Remaining)} remaining",
            (_, SessionPhase.EmergencyPass) =>
                $"{Name} - emergency pass {DurationFormat.Clock(timeline.PassRemaining)}, session {DurationFormat.Clock(timeline.Remaining)} remaining",
            _ => $"{Name} - no session",
        };
        return text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
    }

    /// <summary>Lists the menu entries. "Exit" exists only while no session runs, so the agent cannot be closed to dodge enforcement.</summary>
    /// <param name="snapshot">Latest session snapshot.</param>
    /// <returns>The entries, top to bottom.</returns>
    public static IReadOnlyList<TrayMenuItem> Menu(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<TrayMenuItem> items = [new(TrayCommand.Open, "Open Blocker")];
        if (!snapshot.IsSessionRunning)
        {
            items.Add(new TrayMenuItem(TrayCommand.Exit, "Exit"));
        }

        return items;
    }
}
