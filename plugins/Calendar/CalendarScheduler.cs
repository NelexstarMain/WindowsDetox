// Auto-start decisions. Decide() is a pure function of "now", the entries, what the history already knows and whether a session is
// running; the plugin executes what it returns. The rules:
//   * a planned session starts when its time arrives, once: its history record is written BEFORE the start is requested
//   * a session that is still running blocks a start; the occurrence waits and may still start within the grace window
//   * after sleep, hibernation or boot an occurrence that began less than the grace window ago still starts, for the REMAINING time
//     (it ends when it was planned to end); a later one is recorded as missed and never started
//   * a reminder is shown once, the configured number of minutes before the start

namespace CalendarPlanner;

/// <summary>What the scheduler wants done.</summary>
public enum SchedulerActionKind
{
    /// <summary>Show a reminder toast.</summary>
    Remind,

    /// <summary>Start the session.</summary>
    Start,

    /// <summary>Record that the occurrence was missed.</summary>
    Miss,
}

/// <summary>One thing to do.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Occurrence">The planned session.</param>
/// <param name="StartUtc">Planned start.</param>
/// <param name="Minutes">For a start: the length to start with (the remaining time, at most the planned length).</param>
public sealed record SchedulerAction(SchedulerActionKind Kind, Occurrence Occurrence, DateTimeOffset StartUtc, int Minutes);

/// <summary>The scheduling rules.</summary>
public static class CalendarScheduler
{
    /// <summary>How long after its planned start a session may still be started.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    /// <summary>How far back occurrences without a record are considered (they are recorded as missed).</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

    /// <summary>Decides what to do right now.</summary>
    /// <param name="now">Verified current time.</param>
    /// <param name="zone">The PC's time zone.</param>
    /// <param name="entries">All entries.</param>
    /// <param name="settings">Settings.</param>
    /// <param name="sessionRunning">Whether a session is running.</param>
    /// <param name="recorded">History keys that already have a record.</param>
    /// <param name="reminded">Keys that already got a reminder.</param>
    /// <returns>Actions ordered by planned start; at most one <see cref="SchedulerActionKind.Start"/>.</returns>
    public static IReadOnlyList<SchedulerAction> Decide(
        DateTimeOffset now,
        TimeZoneInfo zone,
        IReadOnlyList<CalendarEntry> entries,
        CalendarSettings settings,
        bool sessionRunning,
        IReadOnlySet<string> recorded,
        IReadOnlySet<string> reminded)
    {
        if (!settings.AutoStart)
        {
            return [];
        }

        int fromDay = CalTime.DayOf(now - Lookback, zone) - 1;
        int toDay = CalTime.DayOf(now + TimeSpan.FromHours(26), zone);
        TimeSpan lead = TimeSpan.FromMinutes(Math.Clamp(settings.ReminderMinutes, 0, 30));
        List<SchedulerAction> actions = [];
        bool startChosen = false;
        foreach (Occurrence occurrence in CalendarRecurrence.Expand(entries.Where(e => e.Kind == EntryKind.Session), fromDay, toDay))
        {
            if (!occurrence.HasTime || recorded.Contains(occurrence.Key))
            {
                continue;
            }

            DateTimeOffset start = CalendarRecurrence.StartUtc(occurrence, zone);
            if (start.ToUnixTimeSeconds() < occurrence.Entry.Created || start < now - Lookback)
            {
                continue;
            }

            if (now < start)
            {
                if (lead > TimeSpan.Zero && start - now <= lead && !reminded.Contains(occurrence.Key))
                {
                    actions.Add(new SchedulerAction(SchedulerActionKind.Remind, occurrence, start, 0));
                }

                continue;
            }

            if (now - start > Grace)
            {
                actions.Add(new SchedulerAction(SchedulerActionKind.Miss, occurrence, start, 0));
                continue;
            }

            DateTimeOffset end = start.AddMinutes(occurrence.Duration);
            TimeSpan remaining = end - now;
            if (remaining < SessionMinimum)
            {
                actions.Add(new SchedulerAction(SchedulerActionKind.Miss, occurrence, start, 0));
            }
            else if (!sessionRunning && !startChosen)
            {
                startChosen = true;
                int minutes = Math.Min(occurrence.Duration, (int)Math.Ceiling(remaining.TotalMinutes));
                actions.Add(new SchedulerAction(SchedulerActionKind.Start, occurrence, start, Math.Clamp(minutes, CalendarLimits.MinSessionMinutes, CalendarLimits.MaxSessionMinutes)));
            }
        }

        return actions;
    }

    private static TimeSpan SessionMinimum => TimeSpan.FromMinutes(CalendarLimits.MinSessionMinutes);

    /// <summary>
    /// The number of planned sessions kept in a row: the latest records that are all <see cref="SessionOutcome.Started"/>, counting back until
    /// the first one that was not. Failed and missed sessions end the streak.
    /// </summary>
    public static int Streak(IEnumerable<HistoryRecord> history)
    {
        int streak = 0;
        foreach (HistoryRecord record in history.OrderByDescending(r => r.StartUnix))
        {
            if (record.Outcome != SessionOutcome.Started)
            {
                break;
            }

            streak++;
        }

        return streak;
    }
}
