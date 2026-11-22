// Pure aggregation and formatting for the page, the widgets and the settings (no state, no I/O).

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>The next planned session.</summary>
/// <param name="Occurrence">The occurrence.</param>
/// <param name="StartUtc">Its start.</param>
/// <param name="EndUtc">Its end.</param>
/// <param name="InProgress">Whether it has started already.</param>
public sealed record NextSession(Occurrence Occurrence, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool InProgress);

/// <summary>Progress of one day.</summary>
/// <param name="Done">Ticked tasks plus started sessions.</param>
/// <param name="Total">Everything planned that day.</param>
public sealed record DayProgress(int Done, int Total);

/// <summary>Everything a render needs, computed once from the store's snapshot.</summary>
public sealed class CalendarView
{
    private readonly Dictionary<string, SessionOutcome> _outcomes;

    private CalendarView(CalendarStore store, DateTimeOffset now)
    {
        Now = now;
        Zone = store.Zone;
        Settings = store.Settings;
        Entries = store.Entries;
        History = store.History;
        ReadOnly = store.ReadOnly;
        Today = CalTime.DayOf(now, Zone);
        _outcomes = History.ToDictionary(r => r.Key, r => r.Outcome, StringComparer.Ordinal);
    }

    /// <summary>The verified current time.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>The PC's zone.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>Settings.</summary>
    public CalendarSettings Settings { get; }

    /// <summary>All entries.</summary>
    public IReadOnlyList<CalendarEntry> Entries { get; }

    /// <summary>The history.</summary>
    public IReadOnlyList<HistoryRecord> History { get; }

    /// <summary>Whether the stored data cannot be changed by this version.</summary>
    public bool ReadOnly { get; }

    /// <summary>Today's day number.</summary>
    public int Today { get; }

    /// <summary>Takes a snapshot of the store.</summary>
    public static CalendarView Of(CalendarStore store) => new(store, store.Time.GetUtcNow());

    /// <summary>Occurrences from <paramref name="fromDay"/> to <paramref name="toDay"/>.</summary>
    public IReadOnlyList<Occurrence> Between(int fromDay, int toDay) => CalendarRecurrence.Expand(Entries, fromDay, toDay);

    /// <summary>Finds one occurrence of an entry by its series day, or <see langword="null"/> when it no longer exists.</summary>
    public Occurrence? Find(OccurrenceRef reference) =>
        Entries.FirstOrDefault(e => e.Id == reference.EntryId) is { } entry ? CalendarRecurrence.OccurrenceOf(entry, reference.OriginalDay) : null;

    /// <summary>What happened to a planned session, if anything.</summary>
    public SessionOutcome? OutcomeOf(Occurrence occurrence) =>
        occurrence.Entry.Kind == EntryKind.Session && _outcomes.TryGetValue(occurrence.Key, out SessionOutcome outcome) ? outcome : null;

    /// <summary>Whether an occurrence counts as done: a ticked task or a started session.</summary>
    public bool IsDone(Occurrence occurrence) =>
        occurrence.Entry.Kind == EntryKind.Task ? occurrence.Done : OutcomeOf(occurrence) == SessionOutcome.Started;

    /// <summary>The next planned session that has not ended and was not missed, looking <paramref name="horizonDays"/> ahead.</summary>
    public NextSession? NextSession(int horizonDays = 120)
    {
        foreach (Occurrence occurrence in Between(Today - 1, Today + horizonDays).Where(o => o.Entry.Kind == EntryKind.Session && o.HasTime))
        {
            DateTimeOffset start = CalendarRecurrence.StartUtc(occurrence, Zone);
            DateTimeOffset end = start.AddMinutes(occurrence.Duration);
            SessionOutcome? outcome = OutcomeOf(occurrence);
            if (end > Now && outcome is not (SessionOutcome.Missed or SessionOutcome.Failed))
            {
                return new NextSession(occurrence, start, end, start <= Now && outcome == SessionOutcome.Started);
            }
        }

        return null;
    }

    /// <summary>Done and total of a day.</summary>
    public DayProgress ProgressOf(int day)
    {
        IReadOnlyList<Occurrence> items = Between(day, day);
        return new DayProgress(items.Count(IsDone), items.Count);
    }

    /// <summary>Minutes of planned sessions in the week containing <paramref name="day"/>.</summary>
    public int PlannedSessionMinutes(int day)
    {
        int start = CalTime.WeekStart(day, Settings.WeekStartsOnSunday);
        return Between(start, start + 6).Where(o => o.Entry.Kind == EntryKind.Session).Sum(o => o.Duration);
    }

    /// <summary>The streak of planned sessions kept.</summary>
    public int Streak => CalendarScheduler.Streak(History);

    /// <summary>The text of an occurrence for a list row: time (or range) and title.</summary>
    public string Line(Occurrence occurrence)
    {
        bool h24 = Settings.Use24Hour;
        if (!occurrence.HasTime)
        {
            return occurrence.Title;
        }

        string start = CalTime.Clock(occurrence.StartMinute, h24);
        if (occurrence.Entry.Kind == EntryKind.Session || occurrence.Duration > 0)
        {
            string end = CalTime.Clock((occurrence.StartMinute + occurrence.Duration) % 1440, h24);
            return string.Create(CultureInfo.InvariantCulture, $"{start}-{end}  {occurrence.Title}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{start}  {occurrence.Title}");
    }

    /// <summary>Secondary text of an occurrence: state of a session, repeat rule, tag.</summary>
    public string Detail(Occurrence occurrence)
    {
        List<string> parts = [];
        if (occurrence.Entry.Kind == EntryKind.Session)
        {
            parts.Add(OutcomeOf(occurrence) switch
            {
                SessionOutcome.Started => "Started",
                SessionOutcome.Missed => "Missed",
                SessionOutcome.Failed => "Could not start",
                _ => "Focus session " + CalTime.Length(occurrence.Duration),
            });
        }

        if (occurrence.Entry.Repeat != RepeatKind.Once)
        {
            parts.Add(RepeatText(occurrence.Entry));
        }

        if (occurrence.IsOverride)
        {
            parts.Add("edited");
        }

        return string.Join(" - ", parts);
    }

    /// <summary>Describes a repeat rule, for example <c>Every Mon, Wed</c> or <c>Every 2 days, 10 times</c>.</summary>
    public static string RepeatText(CalendarEntry entry)
    {
        string text = entry.Repeat switch
        {
            RepeatKind.Once => "Once",
            RepeatKind.Daily => entry.Interval <= 1 ? "Every day" : string.Create(CultureInfo.InvariantCulture, $"Every {entry.Interval} days"),
            _ => WeeklyText(entry),
        };
        return entry.End switch
        {
            EndKind.OnDate when entry.Repeat != RepeatKind.Once => $"{text}, until {CalTime.IsoDate(entry.UntilDay)}",
            EndKind.AfterCount when entry.Repeat != RepeatKind.Once => string.Create(CultureInfo.InvariantCulture, $"{text}, {entry.Count} times"),
            _ => text,
        };
    }

    /// <summary>Tone of a colour tag.</summary>
    public static UiTone ToneOf(int color) => color switch
    {
        1 => UiTone.Accent,
        2 => UiTone.Success,
        3 => UiTone.Warning,
        4 => UiTone.Danger,
        _ => UiTone.Neutral,
    };

    /// <summary>Name of a colour tag.</summary>
    public static string ColorName(int color) => color switch
    {
        1 => "Blue",
        2 => "Green",
        3 => "Amber",
        4 => "Red",
        5 => "Grey",
        _ => "None",
    };

    private static string WeeklyText(CalendarEntry entry)
    {
        int mask = entry.WeekdayMask & 0x7F;
        if (mask == 0)
        {
            mask = 1 << CalTime.Weekday(entry.StartDay);
        }

        string days = string.Join(", ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(CalTime.DayNameByIndex));
        return entry.Interval <= 1 ? $"Every {days}" : string.Create(CultureInfo.InvariantCulture, $"Every {entry.Interval} weeks on {days}");
    }
}
