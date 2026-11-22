// Data model of the Calendar plugin: entries (planned focus sessions and tasks), their repeat rules, exceptions, history and settings.
//
// Everything is local wall-clock time: an entry says "every Monday at 09:00" and keeps meaning 09:00 in the zone the PC is in, across
// daylight saving changes. Days are DateOnly.DayNumber values (0 = Monday 0001-01-01, so day % 7 is the Monday-based weekday).
// Short JSON names keep the stored documents small.

using System.Text.Json.Serialization;

namespace CalendarPlanner;

/// <summary>What an entry is.</summary>
public enum EntryKind
{
    /// <summary>A planned focus session; the plugin starts it by itself when its time comes.</summary>
    Session = 0,

    /// <summary>A task or event the user ticks off.</summary>
    Task = 1,
}

/// <summary>How an entry repeats.</summary>
public enum RepeatKind
{
    /// <summary>Only on its start day.</summary>
    Once = 0,

    /// <summary>Every <see cref="CalendarEntry.Interval"/> days.</summary>
    Daily = 1,

    /// <summary>On the chosen weekdays of every <see cref="CalendarEntry.Interval"/>-th week.</summary>
    Weekly = 2,
}

/// <summary>When a repeating entry stops.</summary>
public enum EndKind
{
    /// <summary>Never.</summary>
    Never = 0,

    /// <summary>After <see cref="CalendarEntry.UntilDay"/> (inclusive).</summary>
    OnDate = 1,

    /// <summary>After <see cref="CalendarEntry.Count"/> occurrences.</summary>
    AfterCount = 2,
}

/// <summary>Limits that keep the stored data small and every render cheap.</summary>
public static class CalendarLimits
{
    /// <summary>Most entries.</summary>
    public const int MaxEntries = 300;

    /// <summary>Longest title.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>Most skipped occurrences per entry.</summary>
    public const int MaxSkips = 120;

    /// <summary>Most single-occurrence edits per entry.</summary>
    public const int MaxOverrides = 60;

    /// <summary>Most remembered done occurrences per entry.</summary>
    public const int MaxDone = 200;

    /// <summary>Most history records (one per started or missed session).</summary>
    public const int MaxHistory = 600;

    /// <summary>Days of history that are kept.</summary>
    public const int HistoryDays = 120;

    /// <summary>Largest repeat interval (days or weeks).</summary>
    public const int MaxInterval = 52;

    /// <summary>Most occurrences of a series that ends after a count.</summary>
    public const int MaxCount = 366;

    /// <summary>Number of colour tags.</summary>
    public const int ColorCount = 6;

    /// <summary>First and last accepted year of a date.</summary>
    public const int MinYear = 2000;

    /// <summary>Last accepted year of a date.</summary>
    public const int MaxYear = 2100;

    /// <summary>Longest span expanded in one query, in days.</summary>
    public const int MaxSpanDays = 400;

    /// <summary>Most occurrences one query returns.</summary>
    public const int MaxOccurrences = 3000;

    /// <summary>Shortest session, in minutes (same as the Setup page).</summary>
    public const int MinSessionMinutes = 15;

    /// <summary>Longest session a plugin may start, in minutes.</summary>
    public const int MaxSessionMinutes = 12 * 60;

    /// <summary>Longest duration of a task with a time, in minutes.</summary>
    public const int MaxTaskMinutes = 24 * 60;

    /// <summary>Most bytes (as the storage counts them) of an unreadable document that are kept for the user to recover.</summary>
    public const int MaxKeptUnreadableBytes = 64 * 1024;

    /// <summary>Days an unreadable copy is kept before it is deleted.</summary>
    public const int UnreadableKeepDays = 30;
}

/// <summary>Edit of one occurrence of a repeating entry.</summary>
public sealed class OccurrenceOverride
{
    /// <summary>Day the occurrence has in the series.</summary>
    [JsonPropertyName("o")]
    public int OriginalDay { get; set; }

    /// <summary>Day it is moved to.</summary>
    [JsonPropertyName("d")]
    public int Day { get; set; }

    /// <summary>Start minute of the day, or -1 for no time.</summary>
    [JsonPropertyName("m")]
    public int Minute { get; set; }

    /// <summary>Duration in minutes.</summary>
    [JsonPropertyName("n")]
    public int Duration { get; set; }

    /// <summary>Title for this occurrence.</summary>
    [JsonPropertyName("t")]
    public string Title { get; set; } = string.Empty;
}

/// <summary>One calendar entry (a series when it repeats).</summary>
public sealed class CalendarEntry
{
    /// <summary>Unique id (never reused).</summary>
    [JsonPropertyName("i")]
    public int Id { get; set; }

    /// <summary>Session or task.</summary>
    [JsonPropertyName("k")]
    public EntryKind Kind { get; set; }

    /// <summary>Title.</summary>
    [JsonPropertyName("t")]
    public string Title { get; set; } = string.Empty;

    /// <summary>First day.</summary>
    [JsonPropertyName("s")]
    public int StartDay { get; set; }

    /// <summary>Start minute of the day (0-1439), or -1 for a task without a time.</summary>
    [JsonPropertyName("m")]
    public int StartMinute { get; set; } = -1;

    /// <summary>Duration in minutes (sessions always; tasks 0 when they have no length).</summary>
    [JsonPropertyName("n")]
    public int Duration { get; set; }

    /// <summary>Repeat rule.</summary>
    [JsonPropertyName("r")]
    public RepeatKind Repeat { get; set; }

    /// <summary>Every N days or weeks.</summary>
    [JsonPropertyName("v")]
    public int Interval { get; set; } = 1;

    /// <summary>Weekdays of a weekly rule as bits (bit 0 = Monday ... bit 6 = Sunday); 0 means the weekday of <see cref="StartDay"/>.</summary>
    [JsonPropertyName("w")]
    public int WeekdayMask { get; set; }

    /// <summary>When the series ends.</summary>
    [JsonPropertyName("e")]
    public EndKind End { get; set; }

    /// <summary>Last day for <see cref="EndKind.OnDate"/>.</summary>
    [JsonPropertyName("u")]
    public int UntilDay { get; set; }

    /// <summary>Occurrence count for <see cref="EndKind.AfterCount"/>.</summary>
    [JsonPropertyName("c")]
    public int Count { get; set; }

    /// <summary>Colour tag 0-5.</summary>
    [JsonPropertyName("g")]
    public int Color { get; set; }

    /// <summary>Unix seconds when the entry was created; occurrences before it never count as missed.</summary>
    [JsonPropertyName("a")]
    public long Created { get; set; }

    /// <summary>Series days (original days) that were skipped.</summary>
    [JsonPropertyName("x")]
    public List<int> Skipped { get; set; } = [];

    /// <summary>Single-occurrence edits.</summary>
    [JsonPropertyName("z")]
    public List<OccurrenceOverride> Overrides { get; set; } = [];

    /// <summary>Series days (original days) of tasks that were ticked off.</summary>
    [JsonPropertyName("f")]
    public List<int> DoneDays { get; set; } = [];

    /// <summary>Returns a deep copy.</summary>
    public CalendarEntry Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        StartDay = StartDay,
        StartMinute = StartMinute,
        Duration = Duration,
        Repeat = Repeat,
        Interval = Interval,
        WeekdayMask = WeekdayMask,
        End = End,
        UntilDay = UntilDay,
        Count = Count,
        Color = Color,
        Created = Created,
        Skipped = [.. Skipped],
        Overrides = [.. Overrides.Select(o => new OccurrenceOverride { OriginalDay = o.OriginalDay, Day = o.Day, Minute = o.Minute, Duration = o.Duration, Title = o.Title })],
        DoneDays = [.. DoneDays],
    };
}

/// <summary>The persisted entries document.</summary>
public sealed class CalendarData
{
    /// <summary>The schema version this build reads and writes; a document with a higher version is only ever read.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Id of the next entry.</summary>
    [JsonPropertyName("n")]
    public int NextId { get; set; } = 1;

    /// <summary>All entries.</summary>
    [JsonPropertyName("e")]
    public List<CalendarEntry> Entries { get; set; } = [];
}

/// <summary>Outcome of a planned session.</summary>
public enum SessionOutcome
{
    /// <summary>The plugin started the session.</summary>
    Started = 0,

    /// <summary>The time passed without a start (PC off, another session running, auto-start off at the time of the check, refused).</summary>
    Missed = 1,

    /// <summary>The service refused to start it (for example an empty allowlist) after several tries.</summary>
    Failed = 2,
}

/// <summary>What happened to one occurrence of a planned session; written once, which is what keeps a session from starting twice.</summary>
public sealed class HistoryRecord
{
    /// <summary><c>entryId:originalDay</c>.</summary>
    [JsonPropertyName("k")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The outcome.</summary>
    [JsonPropertyName("o")]
    public SessionOutcome Outcome { get; set; }

    /// <summary>Planned start as Unix seconds (orders the streak).</summary>
    [JsonPropertyName("t")]
    public long StartUnix { get; set; }
}

/// <summary>The persisted history document.</summary>
public sealed class CalendarHistory
{
    /// <summary>The schema version this build reads and writes; a document with a higher version is only ever read.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Records, oldest first.</summary>
    [JsonPropertyName("h")]
    public List<HistoryRecord> Records { get; set; } = [];
}

/// <summary>User settings.</summary>
public sealed class CalendarSettings
{
    /// <summary>Whether weeks start on Sunday instead of Monday.</summary>
    [JsonPropertyName("s")]
    public bool WeekStartsOnSunday { get; set; }

    /// <summary>Whether times use the 24-hour clock.</summary>
    [JsonPropertyName("h")]
    public bool Use24Hour { get; set; } = true;

    /// <summary>Length suggested for a new session, in minutes.</summary>
    [JsonPropertyName("d")]
    public int DefaultSessionMinutes { get; set; } = 120;

    /// <summary>How long before a session a reminder toast is shown, in minutes (0 = no reminder).</summary>
    [JsonPropertyName("r")]
    public int ReminderMinutes { get; set; } = 1;

    /// <summary>Whether planned sessions start by themselves.</summary>
    [JsonPropertyName("a")]
    public bool AutoStart { get; set; } = true;

    /// <summary>Returns a copy with every value inside its range.</summary>
    public CalendarSettings Sanitized() => new()
    {
        WeekStartsOnSunday = WeekStartsOnSunday,
        Use24Hour = Use24Hour,
        DefaultSessionMinutes = Math.Clamp(DefaultSessionMinutes, CalendarLimits.MinSessionMinutes, CalendarLimits.MaxSessionMinutes),
        ReminderMinutes = Math.Clamp(ReminderMinutes, 0, 30),
        AutoStart = AutoStart,
    };
}
