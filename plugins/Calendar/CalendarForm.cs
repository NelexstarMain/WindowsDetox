// The "Plan" form: field ids, the draft that pre-fills it, and the validation that turns submitted text into an entry, a series
// replacement or the edit of one occurrence. Pure: it reads the entries it is given and returns what the plugin should store.

using System.Globalization;

namespace CalendarPlanner;

/// <summary>Values shown in the Plan form.</summary>
public sealed record CalendarDraft
{
    /// <summary>Entry kind.</summary>
    public EntryKind Kind { get; init; } = EntryKind.Session;

    /// <summary>Title text.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Day.</summary>
    public int Day { get; init; }

    /// <summary>Time text (<c>hh:mm</c>) or empty.</summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>Length in minutes.</summary>
    public int Length { get; init; } = 120;

    /// <summary>Repeat rule.</summary>
    public RepeatKind Repeat { get; init; }

    /// <summary>Interval.</summary>
    public int Every { get; init; } = 1;

    /// <summary>Weekday bits.</summary>
    public int Mask { get; init; }

    /// <summary>End rule.</summary>
    public EndKind End { get; init; }

    /// <summary>End date text or empty.</summary>
    public string Until { get; init; } = string.Empty;

    /// <summary>Occurrence count.</summary>
    public int Count { get; init; } = 10;

    /// <summary>Colour tag.</summary>
    public int Color { get; init; }

    /// <summary>Id of the entry being edited, or 0 for a new one.</summary>
    public int EditId { get; init; }

    /// <summary>Series day of the occurrence being edited.</summary>
    public int EditOriginalDay { get; init; }

    /// <summary>Day the edited occurrence is shown on.</summary>
    public int EditShownDay { get; init; }

    /// <summary>Whether the edit applies to this occurrence only.</summary>
    public bool EditOccurrenceOnly { get; init; }
}

/// <summary>What a submitted form asks the plugin to do.</summary>
/// <param name="Error">Why it was refused; <see langword="null"/> when accepted.</param>
/// <param name="Notice">Something to tell the user after a success.</param>
/// <param name="Add">A new entry.</param>
/// <param name="Replace">A series replaced as a whole.</param>
/// <param name="OverrideEntryId">Entry whose single occurrence is edited.</param>
/// <param name="Override">The edit of that occurrence.</param>
public sealed record FormOutcome(string? Error, string? Notice = null, CalendarEntry? Add = null, CalendarEntry? Replace = null, int OverrideEntryId = 0, OccurrenceOverride? Override = null)
{
    /// <summary>Whether the form was accepted.</summary>
    public bool Accepted => Error is null;

    /// <summary>Creates a refusal.</summary>
    public static FormOutcome Refused(string error) => new(error);
}

/// <summary>Form ids and validation.</summary>
public static class CalendarForm
{
    /// <summary>Id of the form.</summary>
    public const string FormId = "cal-form";

    /// <summary>Id of the kind select.</summary>
    public const string KindId = "cal-f-kind";

    /// <summary>Id of the title field.</summary>
    public const string TitleId = "cal-f-title";

    /// <summary>Id of the date field.</summary>
    public const string DateId = "cal-f-date";

    /// <summary>Id of the time field.</summary>
    public const string TimeId = "cal-f-time";

    /// <summary>Id of the length field.</summary>
    public const string LengthId = "cal-f-length";

    /// <summary>Id of the repeat select.</summary>
    public const string RepeatId = "cal-f-repeat";

    /// <summary>Id of the interval field.</summary>
    public const string EveryId = "cal-f-every";

    /// <summary>Prefix of the weekday checkboxes; the Monday-based index follows.</summary>
    public const string WeekdayPrefix = "cal-f-wd";

    /// <summary>Id of the end select.</summary>
    public const string EndId = "cal-f-end";

    /// <summary>Id of the end date field.</summary>
    public const string UntilId = "cal-f-until";

    /// <summary>Id of the count field.</summary>
    public const string CountId = "cal-f-count";

    /// <summary>Id of the colour select.</summary>
    public const string ColorId = "cal-f-color";

    /// <summary>Id of the scope select shown when a repeating entry is edited.</summary>
    public const string ScopeId = "cal-f-scope";

    /// <summary>Planning horizon for overlap checks, in days.</summary>
    public const int OverlapHorizonDays = 180;

    /// <summary>Builds the draft for a new entry on <paramref name="day"/>.</summary>
    public static CalendarDraft NewDraft(int day, DateTimeOffset now, TimeZoneInfo zone, CalendarSettings settings, EntryKind kind = EntryKind.Session)
    {
        int minute = ((CalTime.MinuteOf(now, zone) / 60) + 1) * 60;
        int shownDay = day;
        if (minute >= 1440)
        {
            minute = 9 * 60;
            shownDay = Math.Max(day, CalTime.DayOf(now, zone) + 1);
        }

        return new CalendarDraft
        {
            Kind = kind,
            Day = shownDay,
            Time = kind == EntryKind.Session || day == CalTime.DayOf(now, zone) ? CalTime.Clock(minute, true) : string.Empty,
            Length = kind == EntryKind.Session ? settings.DefaultSessionMinutes : 0,
        };
    }

    /// <summary>Builds the draft that edits an occurrence (and, with it, its series).</summary>
    public static CalendarDraft EditDraft(Occurrence occurrence)
    {
        CalendarEntry e = occurrence.Entry;
        return new CalendarDraft
        {
            Kind = e.Kind,
            Title = occurrence.Title,
            Day = occurrence.Day,
            Time = occurrence.HasTime ? CalTime.Clock(occurrence.StartMinute, true) : string.Empty,
            Length = occurrence.Duration,
            Repeat = e.Repeat,
            Every = e.Interval,
            Mask = e.WeekdayMask != 0 ? e.WeekdayMask : 1 << CalTime.Weekday(e.StartDay),
            End = e.End,
            Until = e.End == EndKind.OnDate ? CalTime.IsoDate(e.UntilDay) : string.Empty,
            Count = e.Count > 0 ? e.Count : 10,
            Color = e.Color,
            EditId = e.Id,
            EditOriginalDay = occurrence.OriginalDay,
            EditShownDay = occurrence.Day,
            EditOccurrenceOnly = e.Repeat != RepeatKind.Once,
        };
    }

    /// <summary>Validates a submitted form.</summary>
    /// <param name="values">Field id to text.</param>
    /// <param name="editing">The draft the form was showing (its edit target decides between add and edit).</param>
    /// <param name="entries">The current entries.</param>
    /// <param name="now">Verified now.</param>
    /// <param name="zone">The PC's zone.</param>
    /// <param name="settings">Settings.</param>
    /// <returns>The outcome.</returns>
    public static FormOutcome Submit(
        IReadOnlyDictionary<string, string> values,
        CalendarDraft editing,
        IReadOnlyList<CalendarEntry> entries,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CalendarSettings settings)
    {
        string Get(string id) => values.TryGetValue(id, out string? v) ? v ?? string.Empty : string.Empty;
        int today = CalTime.DayOf(now, zone);
        CalendarEntry? existing = editing.EditId > 0 ? entries.FirstOrDefault(e => e.Id == editing.EditId) : null;
        if (editing.EditId > 0 && existing is null)
        {
            return FormOutcome.Refused("That entry no longer exists.");
        }

        // The kind and the repeat rule are chosen with buttons next to the form, so the form may not carry them: the draft has them then.
        EntryKind kind = !values.ContainsKey(KindId) ? editing.Kind : Get(KindId) switch
        {
            "session" => EntryKind.Session,
            "task" => EntryKind.Task,
            _ => (EntryKind)(-1),
        };
        if (!Enum.IsDefined(kind))
        {
            return FormOutcome.Refused("Choose whether this is a session or a task.");
        }

        string title = CalendarStore.CleanTitle(Get(TitleId));
        if (Get(TitleId).Trim().Length == 0)
        {
            if (kind == EntryKind.Task)
            {
                return FormOutcome.Refused("Give the task a title.");
            }

            title = "Focus session";
        }

        if (!CalTime.TryParseDate(Get(DateId), out int day))
        {
            return FormOutcome.Refused("Enter the date as yyyy-mm-dd, for example 2026-03-09.");
        }

        string? notice = CalTime.IsoDate(day) == Get(DateId).Trim().Replace('/', '-').Replace('.', '-') ? null : $"The date was adjusted to {CalTime.IsoDate(day)}.";
        int minute = -1;
        if (Get(TimeId).Trim().Length > 0)
        {
            if (!CalTime.TryParseTime(Get(TimeId), out minute))
            {
                return FormOutcome.Refused("Enter the time as hh:mm, for example 09:30.");
            }
        }
        else if (kind == EntryKind.Session)
        {
            return FormOutcome.Refused("A session needs a start time.");
        }

        if (!TryNumber(Get(LengthId), out int length))
        {
            return FormOutcome.Refused("Enter the length in minutes.");
        }

        if (kind == EntryKind.Session)
        {
            if (length is < CalendarLimits.MinSessionMinutes or > CalendarLimits.MaxSessionMinutes)
            {
                return FormOutcome.Refused("A session lasts from 15 minutes to 12 hours.");
            }
        }
        else if (length is < 0 or > CalendarLimits.MaxTaskMinutes)
        {
            return FormOutcome.Refused("A task lasts from 0 minutes to 24 hours.");
        }
        else if (minute < 0)
        {
            length = 0;
        }

        RepeatKind repeat = !values.ContainsKey(RepeatId) ? editing.Repeat : Get(RepeatId) switch
        {
            "once" => RepeatKind.Once,
            "daily" => RepeatKind.Daily,
            "weekly" => RepeatKind.Weekly,
            _ => (RepeatKind)(-1),
        };
        if (!Enum.IsDefined(repeat))
        {
            return FormOutcome.Refused("Choose how often it repeats.");
        }

        int every = 1;
        if ((values.ContainsKey(EveryId) || repeat != RepeatKind.Once) && (!TryNumber(Get(EveryId), out every) || every is < 1 or > CalendarLimits.MaxInterval))
        {
            return FormOutcome.Refused($"Repeat every 1 to {CalendarLimits.MaxInterval}.");
        }

        int mask = 0;
        for (int i = 0; i < 7; i++)
        {
            if (Get(WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture)) == "true")
            {
                mask |= 1 << i;
            }
        }

        EndKind end = Get(EndId) switch
        {
            "date" => EndKind.OnDate,
            "count" => EndKind.AfterCount,
            _ => EndKind.Never,
        };
        int until = 0;
        int count = 0;
        if (repeat == RepeatKind.Once)
        {
            end = EndKind.Never;
        }
        else if (end == EndKind.OnDate)
        {
            if (!CalTime.TryParseDate(Get(UntilId), out until))
            {
                return FormOutcome.Refused("Enter the end date as yyyy-mm-dd.");
            }
        }
        else if (end == EndKind.AfterCount && (!TryNumber(Get(CountId), out count) || count is < 1 or > CalendarLimits.MaxCount))
        {
            return FormOutcome.Refused($"Repeat 1 to {CalendarLimits.MaxCount} times.");
        }

        if (!int.TryParse(Get(ColorId), NumberStyles.None, CultureInfo.InvariantCulture, out int color) || color >= CalendarLimits.ColorCount)
        {
            color = 0;
        }

        bool occurrenceOnly = existing is not null && existing.Repeat != RepeatKind.Once && Get(ScopeId) != "series";
        CalendarEntry candidate = new()
        {
            Id = existing?.Id ?? 0,
            Kind = kind,
            Title = title,
            StartDay = day,
            StartMinute = minute,
            Duration = length,
            Repeat = repeat,
            Interval = every,
            WeekdayMask = repeat == RepeatKind.Weekly ? mask : 0,
            End = end,
            UntilDay = end == EndKind.OnDate ? until : 0,
            Count = end == EndKind.AfterCount ? count : 0,
            Color = color,
            Created = existing?.Created ?? now.ToUnixTimeSeconds(),
        };

        if (occurrenceOnly)
        {
            return EditOccurrence(existing!, candidate, editing, entries, now, zone, settings, today, notice);
        }

        if (existing is not null)
        {
            // Moving the date of an occurrence moves the whole series by the same number of days.
            int shift = day - editing.EditShownDay;
            candidate.StartDay = Math.Clamp(existing.StartDay + shift, new DateOnly(CalendarLimits.MinYear, 1, 1).DayNumber, new DateOnly(CalendarLimits.MaxYear, 12, 31).DayNumber);
            if (end == EndKind.OnDate)
            {
                if (!CalTime.TryParseDate(Get(UntilId), out until))
                {
                    return FormOutcome.Refused("Enter the end date as yyyy-mm-dd.");
                }

                candidate.UntilDay = until;
            }

            if (existing.Repeat == RepeatKind.Once && repeat == RepeatKind.Once)
            {
                candidate.StartDay = day;
            }

            candidate.Skipped = [.. existing.Skipped];
            candidate.DoneDays = [.. existing.DoneDays];
        }

        if (end == EndKind.OnDate && candidate.UntilDay < candidate.StartDay)
        {
            return FormOutcome.Refused("The end date is before the first day.");
        }

        if (kind == EntryKind.Session)
        {
            if (FutureProblem(candidate, now, zone, today) is { } past)
            {
                return FormOutcome.Refused(past);
            }

            string? overlap = CalendarRecurrence.FindSessionOverlap(candidate, entries, Math.Max(candidate.StartDay, today), OverlapHorizonDays, zone, settings.Use24Hour);
            if (overlap is not null)
            {
                return FormOutcome.Refused(overlap);
            }
        }

        return existing is null ? new FormOutcome(null, notice, Add: candidate) : new FormOutcome(null, notice, Replace: candidate);
    }

    private static FormOutcome EditOccurrence(
        CalendarEntry existing,
        CalendarEntry candidate,
        CalendarDraft editing,
        IReadOnlyList<CalendarEntry> entries,
        DateTimeOffset now,
        TimeZoneInfo zone,
        CalendarSettings settings,
        int today,
        string? notice)
    {
        if (candidate.Kind != existing.Kind)
        {
            return FormOutcome.Refused("Change a single occurrence's kind by editing all occurrences.");
        }

        var change = new OccurrenceOverride
        {
            OriginalDay = editing.EditOriginalDay,
            Day = candidate.StartDay,
            Minute = candidate.StartMinute,
            Duration = candidate.Duration,
            Title = candidate.Title,
        };
        if (!CalendarRecurrence.IsSeriesDay(existing, change.OriginalDay))
        {
            return FormOutcome.Refused("That occurrence no longer exists.");
        }

        if (existing.Kind == EntryKind.Session)
        {
            CalendarEntry probe = existing.Clone();
            _ = probe.Overrides.RemoveAll(o => o.OriginalDay == change.OriginalDay);
            probe.Overrides.Add(change);
            DateTimeOffset start = CalTime.ToUtc(change.Day, change.Minute, zone);
            if (start <= now)
            {
                return FormOutcome.Refused("That time has already passed.");
            }

            string? overlap = CalendarRecurrence.FindSessionOverlap(probe, entries, Math.Max(Math.Min(change.Day, change.OriginalDay), today), OverlapHorizonDays, zone, settings.Use24Hour);
            if (overlap is not null)
            {
                return FormOutcome.Refused(overlap);
            }
        }

        return new FormOutcome(null, notice, OverrideEntryId: existing.Id, Override: change);
    }

    /// <summary>Why a session series has nothing left to start, or <see langword="null"/> when it still has a future occurrence.</summary>
    public static string? FutureProblem(CalendarEntry candidate, DateTimeOffset now, TimeZoneInfo zone, int today)
    {
        bool anyFuture = CalendarRecurrence.ExpandEntry(candidate, today - 1, today + CalendarLimits.MaxSpanDays - 2)
            .Any(o => o.HasTime && CalendarRecurrence.StartUtc(o, zone) > now);
        if (anyFuture)
        {
            return null;
        }

        if (candidate.Repeat == RepeatKind.Once)
        {
            return "That time has already passed.";
        }

        // A series that only starts far in the future, or a daily rule with a huge interval, may have no occurrence in the window: look further.
        bool later = candidate.End == EndKind.Never || CalendarRecurrence.BaseDays(candidate, today, today + (CalendarLimits.MaxCount * 7 * CalendarLimits.MaxInterval)).Any(d => d > today || CalTime.ToUtc(d, Math.Max(0, candidate.StartMinute), zone) > now);
        return later ? null : "Every occurrence is in the past.";
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
        {
            return false;
        }

        value = (int)Math.Round(number);
        return true;
    }
}
