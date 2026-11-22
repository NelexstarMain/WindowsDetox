// Recurrence: expands entries into occurrences for a range of days, applies skips and single-occurrence edits, finds overlapping
// sessions and validates what the user typed. Pure functions over the model; no state, no I/O.

using System.Globalization;

namespace CalendarPlanner;

/// <summary>One concrete occurrence of an entry on a day.</summary>
/// <param name="Entry">The entry (series) it belongs to.</param>
/// <param name="OriginalDay">The day this occurrence has in the series (differs from <paramref name="Day"/> when it was moved).</param>
/// <param name="Day">The day it is shown on.</param>
/// <param name="StartMinute">Start minute of the day, or -1 for no time.</param>
/// <param name="Duration">Length in minutes.</param>
/// <param name="Title">Title of this occurrence.</param>
/// <param name="Done">Whether a task occurrence is ticked off.</param>
/// <param name="IsOverride">Whether this occurrence was edited on its own.</param>
public sealed record Occurrence(CalendarEntry Entry, int OriginalDay, int Day, int StartMinute, int Duration, string Title, bool Done, bool IsOverride)
{
    /// <summary>Gets the key under which the history remembers this occurrence.</summary>
    public string Key => Keyed(Entry.Id, OriginalDay);

    /// <summary>Gets whether the occurrence has a start time.</summary>
    public bool HasTime => StartMinute >= 0;

    /// <summary>Builds a history key.</summary>
    public static string Keyed(int entryId, int originalDay) => string.Create(CultureInfo.InvariantCulture, $"{entryId}:{originalDay}");
}

/// <summary>Expansion and overlap detection.</summary>
public static class CalendarRecurrence
{
    private const int MaxIterations = 20000;

    /// <summary>Expands <paramref name="entries"/> into occurrences on days <paramref name="fromDay"/> to <paramref name="toDay"/> (inclusive), ordered by day, time (untimed first) and id.</summary>
    public static IReadOnlyList<Occurrence> Expand(IEnumerable<CalendarEntry> entries, int fromDay, int toDay)
    {
        List<Occurrence> all = [];
        if (toDay < fromDay)
        {
            return all;
        }

        toDay = Math.Min(toDay, fromDay + CalendarLimits.MaxSpanDays - 1);
        foreach (CalendarEntry entry in entries)
        {
            all.AddRange(ExpandEntry(entry, fromDay, toDay));
            if (all.Count >= CalendarLimits.MaxOccurrences)
            {
                break;
            }
        }

        return [.. all.OrderBy(o => o.Day).ThenBy(o => o.StartMinute).ThenBy(o => o.Entry.Id).Take(CalendarLimits.MaxOccurrences)];
    }

    /// <summary>Expands one entry.</summary>
    public static IReadOnlyList<Occurrence> ExpandEntry(CalendarEntry entry, int fromDay, int toDay)
    {
        List<Occurrence> result = [];
        foreach (int day in BaseDays(entry, fromDay, toDay))
        {
            if (!entry.Skipped.Contains(day) && !entry.Overrides.Any(o => o.OriginalDay == day))
            {
                result.Add(new Occurrence(entry, day, day, entry.StartMinute, entry.Duration, entry.Title, entry.DoneDays.Contains(day), IsOverride: false));
            }
        }

        // An edited occurrence is shown on its new day, which may lie in the range although its series day does not.
        foreach (OccurrenceOverride moved in entry.Overrides)
        {
            if (moved.Day >= fromDay && moved.Day <= toDay && !entry.Skipped.Contains(moved.OriginalDay) && IsSeriesDay(entry, moved.OriginalDay))
            {
                result.Add(new Occurrence(entry, moved.OriginalDay, moved.Day, moved.Minute, moved.Duration, moved.Title, entry.DoneDays.Contains(moved.OriginalDay), IsOverride: true));
            }
        }

        return result;
    }

    /// <summary>Finds the occurrence of <paramref name="entry"/> that has <paramref name="originalDay"/> in its series, wherever it was moved; <see langword="null"/> when there is none or it was skipped.</summary>
    public static Occurrence? OccurrenceOf(CalendarEntry entry, int originalDay)
    {
        if (entry.Skipped.Contains(originalDay) || !IsSeriesDay(entry, originalDay))
        {
            return null;
        }

        bool done = entry.DoneDays.Contains(originalDay);
        OccurrenceOverride? edit = entry.Overrides.FirstOrDefault(o => o.OriginalDay == originalDay);
        return edit is null
            ? new Occurrence(entry, originalDay, originalDay, entry.StartMinute, entry.Duration, entry.Title, done, IsOverride: false)
            : new Occurrence(entry, originalDay, edit.Day, edit.Minute, edit.Duration, edit.Title, done, IsOverride: true);
    }

    /// <summary>Whether <paramref name="day"/> is a day of the series (ignoring skips and edits).</summary>
    public static bool IsSeriesDay(CalendarEntry entry, int day) => BaseDays(entry, day, day).Any();

    /// <summary>Gets the series days of an entry in <paramref name="fromDay"/> to <paramref name="toDay"/>, ignoring skips and edits.</summary>
    public static IEnumerable<int> BaseDays(CalendarEntry entry, int fromDay, int toDay)
    {
        int start = entry.StartDay;
        int last = entry.End == EndKind.OnDate ? entry.UntilDay : int.MaxValue;
        int maxCount = entry.End == EndKind.AfterCount ? Math.Clamp(entry.Count, 1, CalendarLimits.MaxCount) : int.MaxValue;
        int hi = Math.Min(toDay, last);
        int interval = Math.Clamp(entry.Interval, 1, CalendarLimits.MaxInterval);
        if (hi < Math.Max(fromDay, start))
        {
            yield break;
        }

        switch (entry.Repeat)
        {
            case RepeatKind.Once:
                if (start >= fromDay && start <= hi)
                {
                    yield return start;
                }

                yield break;

            case RepeatKind.Daily:
                {
                    int kMin = fromDay <= start ? 0 : (fromDay - start + interval - 1) / interval;
                    int kMax = (hi - start) / interval;
                    kMax = Math.Min(kMax, maxCount - 1);
                    for (int k = kMin; k <= kMax && k - kMin < MaxIterations; k++)
                    {
                        yield return start + (k * interval);
                    }

                    yield break;
                }

            default:
                {
                    int mask = entry.WeekdayMask & 0x7F;
                    if (mask == 0)
                    {
                        mask = 1 << CalTime.Weekday(start);
                    }

                    int anchor = start - CalTime.Weekday(start);
                    int firstWeek = maxCount == int.MaxValue ? Math.Max(0, (fromDay - anchor) / 7) : 0;
                    if (maxCount == int.MaxValue)
                    {
                        firstWeek = (firstWeek + interval - 1) / interval * interval;
                    }

                    int ordinal = 0;
                    int iterations = 0;
                    for (int week = firstWeek; anchor + (week * 7) <= hi && iterations++ < MaxIterations; week += interval)
                    {
                        for (int weekday = 0; weekday < 7; weekday++)
                        {
                            int day = anchor + (week * 7) + weekday;
                            if ((mask & (1 << weekday)) == 0 || day < start)
                            {
                                continue;
                            }

                            if (maxCount != int.MaxValue && ++ordinal > maxCount)
                            {
                                yield break;
                            }

                            if (day >= fromDay && day <= hi)
                            {
                                yield return day;
                            }
                        }
                    }

                    yield break;
                }
        }
    }

    /// <summary>The instant a timed occurrence starts.</summary>
    public static DateTimeOffset StartUtc(Occurrence occurrence, TimeZoneInfo zone) => CalTime.ToUtc(occurrence.Day, Math.Max(0, occurrence.StartMinute), zone);

    /// <summary>The instant a timed occurrence ends.</summary>
    public static DateTimeOffset EndUtc(Occurrence occurrence, TimeZoneInfo zone) => StartUtc(occurrence, zone).AddMinutes(occurrence.Duration);

    /// <summary>
    /// Finds a planned session that overlaps <paramref name="candidate"/>'s sessions within the next <paramref name="horizonDays"/> days from
    /// <paramref name="fromDay"/>. Only sessions are compared; <paramref name="candidate"/> itself (same id) is left out of <paramref name="others"/>.
    /// </summary>
    /// <returns>A short description of the first conflict, or <see langword="null"/>.</returns>
    public static string? FindSessionOverlap(CalendarEntry candidate, IEnumerable<CalendarEntry> others, int fromDay, int horizonDays, TimeZoneInfo zone, bool use24Hour)
    {
        if (candidate.Kind != EntryKind.Session)
        {
            return null;
        }

        int toDay = fromDay + Math.Clamp(horizonDays, 1, CalendarLimits.MaxSpanDays - 2);
        IReadOnlyList<Occurrence> mine = ExpandEntry(candidate, fromDay, toDay);
        return FindOverlap(mine, others.Where(o => o.Id != candidate.Id && o.Kind == EntryKind.Session), fromDay, toDay, zone, use24Hour);
    }

    /// <summary>Finds a session of <paramref name="others"/> that overlaps one of <paramref name="mine"/>.</summary>
    public static string? FindOverlap(IReadOnlyList<Occurrence> mine, IEnumerable<CalendarEntry> others, int fromDay, int toDay, TimeZoneInfo zone, bool use24Hour)
    {
        IReadOnlyList<Occurrence> theirs = Expand(others, fromDay - 1, toDay + 1);
        foreach (Occurrence a in mine.Where(o => o.HasTime))
        {
            DateTimeOffset aStart = StartUtc(a, zone);
            DateTimeOffset aEnd = EndUtc(a, zone);
            foreach (Occurrence b in theirs.Where(o => o.HasTime && o.Entry.Kind == EntryKind.Session))
            {
                if (aStart < EndUtc(b, zone) && StartUtc(b, zone) < aEnd)
                {
                    return $"Overlaps \"{b.Title}\" on {CalTime.ShortDate(b.Day)} at {CalTime.Clock(b.StartMinute, use24Hour)}.";
                }
            }
        }

        return null;
    }
}
