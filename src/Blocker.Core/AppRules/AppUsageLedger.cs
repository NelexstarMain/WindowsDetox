using Blocker.Contracts;

namespace Blocker.Core.AppRules;

/// <summary>Usage of one app: the seconds counted on <paramref name="Day"/> and in the week that starts on <paramref name="Week"/> (a Monday).</summary>
/// <param name="Day">Local day of the last update.</param>
/// <param name="DaySeconds">Seconds counted on that day.</param>
/// <param name="Week">Monday of the week of the last update.</param>
/// <param name="WeekSeconds">Seconds counted in that week.</param>
public sealed record AppUsageEntry(DateOnly Day, long DaySeconds, DateOnly Week, long WeekSeconds);

/// <summary>The persistable state of <see cref="AppUsageLedger"/>.</summary>
/// <param name="Entries">Usage by app key (see <c>AppRuleIdentity</c>).</param>
public sealed record AppUsageState(Dictionary<string, AppUsageEntry> Entries);

/// <summary>
/// Foreground time per app, split by local day and by week (Monday to Sunday). Counters roll when the local date moves forward; a clock that
/// moved backwards never rolls them back, so winding the clock back cannot give time back. Mutated only by its owner (not thread-safe).
/// </summary>
/// <param name="initial">State to continue from, or <see langword="null"/> for none.</param>
public sealed class AppUsageLedger(AppUsageState? initial = null)
{
    private readonly Dictionary<string, AppUsageEntry> _entries = initial is null ? [] : new(initial.Entries, StringComparer.Ordinal);

    /// <summary>Gets a snapshot of the state.</summary>
    public AppUsageState State => new(new Dictionary<string, AppUsageEntry>(_entries, StringComparer.Ordinal));

    /// <summary>Counts <paramref name="seconds"/> for <paramref name="key"/>; amounts that are not positive are ignored.</summary>
    /// <param name="key">App key.</param>
    /// <param name="seconds">Seconds to add.</param>
    /// <param name="localNow">Local wall-clock time.</param>
    public void Add(string key, long seconds, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (seconds <= 0)
        {
            return;
        }

        DateOnly today = DateOnly.FromDateTime(localNow);
        DateOnly week = WeekStart(today);
        AppUsageEntry entry = _entries.GetValueOrDefault(key) ?? new AppUsageEntry(today, 0, week, 0);
        if (today > entry.Day)
        {
            entry = entry with { Day = today, DaySeconds = 0 };
        }

        if (week > entry.Week)
        {
            entry = entry with { Week = week, WeekSeconds = 0 };
        }

        _entries[key] = entry with { DaySeconds = entry.DaySeconds + seconds, WeekSeconds = entry.WeekSeconds + seconds };
    }

    /// <summary>Gets the seconds counted for <paramref name="key"/> on the local day of <paramref name="localNow"/>.</summary>
    public long DaySeconds(string key, DateTime localNow) =>
        _entries.TryGetValue(key, out AppUsageEntry? entry) && DateOnly.FromDateTime(localNow) <= entry.Day ? entry.DaySeconds : 0;

    /// <summary>Gets the seconds counted for <paramref name="key"/> in the week of <paramref name="localNow"/>.</summary>
    public long WeekSeconds(string key, DateTime localNow) =>
        _entries.TryGetValue(key, out AppUsageEntry? entry) && WeekStart(DateOnly.FromDateTime(localNow)) <= entry.Week ? entry.WeekSeconds : 0;

    /// <summary>Gets the seconds counted in the period a pool of <paramref name="mode"/> uses; 0 without a pool.</summary>
    public long Used(string key, AppBudgetMode mode, DateTime localNow) => mode switch
    {
        AppBudgetMode.None => 0,
        AppBudgetMode.Weekly => WeekSeconds(key, localNow),
        _ => DaySeconds(key, localNow),
    };

    /// <summary>Adds the usage of <paramref name="from"/> to <paramref name="to"/> and forgets <paramref name="from"/>; nothing happens when <paramref name="from"/> has none.</summary>
    public void Move(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!_entries.Remove(from, out AppUsageEntry? moved))
        {
            return;
        }

        _entries[to] = _entries.TryGetValue(to, out AppUsageEntry? existing) ? Merge(existing, moved) : moved;
    }

    /// <summary>Forgets the entries that are stale for both the day and the week; entries of apps without a rule are kept until then.</summary>
    /// <param name="localNow">Local wall-clock time.</param>
    public void Prune(DateTime localNow)
    {
        DateOnly today = DateOnly.FromDateTime(localNow);
        DateOnly week = WeekStart(today);
        foreach (string key in _entries.Where(p => p.Value.Day < today && p.Value.Week < week).Select(static p => p.Key).ToList())
        {
            _ = _entries.Remove(key);
        }
    }

    private static AppUsageEntry Merge(AppUsageEntry a, AppUsageEntry b)
    {
        DateOnly day = a.Day > b.Day ? a.Day : b.Day;
        DateOnly week = a.Week > b.Week ? a.Week : b.Week;
        return new AppUsageEntry(
            day,
            (a.Day == day ? a.DaySeconds : 0) + (b.Day == day ? b.DaySeconds : 0),
            week,
            (a.Week == week ? a.WeekSeconds : 0) + (b.Week == week ? b.WeekSeconds : 0));
    }

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-AppRuleEvaluator.DayIndex(day.ToDateTime(TimeOnly.MinValue)));
}
