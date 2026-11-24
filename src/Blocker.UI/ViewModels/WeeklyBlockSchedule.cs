using Blocker.Contracts;

namespace Blocker.UI.ViewModels;

/// <summary>
/// The blocked ranges of one app across a week, kept sorted by day and start with overlapping or touching ranges merged,
/// so the grid never shows two blocks on top of each other and the service always receives a canonical list.
/// </summary>
internal sealed class WeeklyBlockSchedule
{
    /// <summary>Number of weekdays.</summary>
    public const int DaysPerWeek = 7;

    /// <summary>Minutes in a day; the largest <see cref="WeeklyRangeDto.EndMinute"/>.</summary>
    public const int MinutesPerDay = 1440;

    private readonly List<WeeklyRangeDto> _ranges = [];

    /// <summary>Creates a schedule from stored ranges; invalid ranges are dropped and the rest are merged.</summary>
    /// <param name="ranges">Stored ranges in any order.</param>
    public WeeklyBlockSchedule(IEnumerable<WeeklyRangeDto> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        foreach (WeeklyRangeDto range in ranges)
        {
            _ = Add(range.Day, range.StartMinute, range.EndMinute);
        }
    }

    /// <summary>Gets the ranges, sorted by day and start, without overlaps.</summary>
    public IReadOnlyList<WeeklyRangeDto> Ranges => _ranges;

    /// <summary>Returns whether a range lies inside the week: day 0 to 6 and 0 &lt;= start &lt; end &lt;= 1440.</summary>
    /// <param name="day">Weekday, 0 = Monday.</param>
    /// <param name="start">First blocked minute.</param>
    /// <param name="end">Minute the block ends, exclusive.</param>
    /// <returns><see langword="true"/> when the range is usable.</returns>
    public static bool IsValid(int day, int start, int end) =>
        day is >= 0 and < DaysPerWeek && start >= 0 && end <= MinutesPerDay && start < end;

    /// <summary>Blocks a range, merging it with every range it overlaps or touches.</summary>
    /// <param name="day">Weekday, 0 = Monday.</param>
    /// <param name="start">First blocked minute.</param>
    /// <param name="end">Minute the block ends, exclusive.</param>
    /// <returns>The merged range that now contains it, or <see langword="null"/> when the range is invalid.</returns>
    public WeeklyRangeDto? Add(int day, int start, int end)
    {
        if (!IsValid(day, start, end))
        {
            return null;
        }

        int mergedStart = start;
        int mergedEnd = end;
        _ = _ranges.RemoveAll(r =>
        {
            bool touches = r.Day == day && r.StartMinute <= end && r.EndMinute >= start;
            if (touches)
            {
                mergedStart = Math.Min(mergedStart, r.StartMinute);
                mergedEnd = Math.Max(mergedEnd, r.EndMinute);
            }

            return touches;
        });
        WeeklyRangeDto merged = new(day, mergedStart, mergedEnd);
        _ranges.Add(merged);
        _ranges.Sort(static (a, b) => a.Day != b.Day ? a.Day.CompareTo(b.Day) : a.StartMinute.CompareTo(b.StartMinute));
        return merged;
    }

    /// <summary>Removes a range.</summary>
    /// <param name="range">The range as listed in <see cref="Ranges"/>.</param>
    /// <returns><see langword="true"/> when it was present.</returns>
    public bool Remove(WeeklyRangeDto range) => _ranges.Remove(range);

    /// <summary>Moves or resizes a range. When the new position is invalid the schedule stays unchanged.</summary>
    /// <param name="old">The range as listed in <see cref="Ranges"/>.</param>
    /// <param name="day">New weekday.</param>
    /// <param name="start">New first blocked minute.</param>
    /// <param name="end">New end minute, exclusive.</param>
    /// <returns>The merged range at the new position, or <see langword="null"/> when nothing changed.</returns>
    public WeeklyRangeDto? Replace(WeeklyRangeDto old, int day, int start, int end)
    {
        if (!IsValid(day, start, end) || !_ranges.Remove(old))
        {
            return null;
        }

        return Add(day, start, end);
    }

    /// <summary>Removes every range.</summary>
    public void Clear() => _ranges.Clear();
}
