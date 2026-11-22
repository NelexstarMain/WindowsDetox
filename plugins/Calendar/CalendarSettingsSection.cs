// Settings: week start, clock, default session length, reminder lead time, auto-start and clearing past entries.

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>Settings section. Requires "ui.settings".</summary>
public sealed class CalendarSettingsSection(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), ISettingsSectionPlugin
{
    /// <inheritdoc/>
    public string Title => "Calendar";

    /// <inheritdoc/>
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.FromResult(Build(Store.Settings, State, Store.Entries.Count(e => CalendarStore.IsPast(e, Store.Today)), Store.ReadOnly));
    }

    /// <inheritdoc/>
    public override ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return Controller.HandleSettingsAsync(action, cancellationToken);
    }

    /// <summary>Builds the section.</summary>
    public static UiNode Build(CalendarSettings s, CalendarUiState state, int pastEntries, bool readOnly = false)
    {
        UiButton clear = state.IsArmed(CalendarIds.SetClear)
            ? Ui.Button(CalendarIds.SetClear, "Click again to confirm", UiButtonVariant.Warning, UiIcon.Warning)
            : new UiButton(CalendarIds.SetClear, "Clear past entries", UiButtonVariant.Secondary, UiIcon.Dismiss, pastEntries > 0);
        List<UiNode> body = [];
        if (readOnly)
        {
            body.Add(Ui.Text(CalendarController.ReadOnlyText, UiTextStyle.Caption, UiTone.Warning));
        }

        body.AddRange(
        [
            Ui.Text("Planned sessions start by themselves when their time comes, using your saved allowlist. A started session cannot be shortened.", UiTextStyle.Caption),
            Ui.Toggle(CalendarIds.SetAuto, "Start planned sessions automatically", s.AutoStart, s.AutoStart ? "On" : "Off: sessions are shown but never started"),
            Ui.Toggle(CalendarIds.SetWeek, "Week starts on Sunday", s.WeekStartsOnSunday, s.WeekStartsOnSunday ? "Sunday" : "Monday"),
            Ui.Toggle(CalendarIds.Set24h, "24-hour clock", s.Use24Hour, s.Use24Hour ? "09:30" : "9:30 AM"),
            Ui.Text($"Default session length: {CalTime.Length(s.DefaultSessionMinutes)}"),
            Ui.Row(
                new UiButton(CalendarIds.SetLengthDown, "Length -15 min", UiButtonVariant.Secondary, IsEnabled: s.DefaultSessionMinutes > CalendarLimits.MinSessionMinutes),
                new UiButton(CalendarIds.SetLengthUp, "Length +15 min", UiButtonVariant.Secondary, IsEnabled: s.DefaultSessionMinutes < CalendarLimits.MaxSessionMinutes)),
            Ui.Text(s.ReminderMinutes == 0 ? "Reminder before a session: off" : $"Reminder before a session: {s.ReminderMinutes.ToString(CultureInfo.InvariantCulture)} min"),
            Ui.Row(
                new UiButton(CalendarIds.SetLeadDown, "Reminder -1 min", UiButtonVariant.Secondary, IsEnabled: s.ReminderMinutes > 0),
                new UiButton(CalendarIds.SetLeadUp, "Reminder +1 min", UiButtonVariant.Secondary, IsEnabled: s.ReminderMinutes < 30)),
            Ui.Text(pastEntries == 0 ? "There are no past entries to clear." : $"Clearing removes {pastEntries} finished {(pastEntries == 1 ? "entry" : "entries")} (one-time entries before today and series that ended). Your streak is kept.", UiTextStyle.Caption),
        ]);
        if (state.Message is { } message)
        {
            body.Add(Ui.Text(message.Text, UiTextStyle.Caption, message.IsError ? UiTone.Danger : UiTone.Success));
        }

        body.Add(clear);
        return Ui.Column([.. body]);
    }
}
