// Aggregation (stored day records -> numbers for the UI), deterministic example data and text formatting.

using System.Globalization;

namespace FocusInsights;

/// <summary>A blocked app and how often it was blocked.</summary>
/// <param name="App">Lower-case app name.</param>
/// <param name="Count">Blocked attempts.</param>
public sealed record AppCount(string App, int Count);

/// <summary>Everything the UI shows, derived from the stored aggregates.</summary>
public sealed record InsightsView
{
    /// <summary>The local day number the view was built for.</summary>
    public int Today { get; init; }

    /// <summary>Whether anything at all was recorded.</summary>
    public bool HasData { get; init; }

    /// <summary>Focus seconds of the last 7 days (today included).</summary>
    public int FocusSeconds7d { get; init; }

    /// <summary>Focus seconds of today.</summary>
    public int FocusSecondsToday { get; init; }

    /// <summary>Completed sessions of the last 7 days.</summary>
    public int Completed7d { get; init; }

    /// <summary>Blocked attempts of the last 7 days.</summary>
    public int Blocked7d { get; init; }

    /// <summary>Days in a row with a completed session, ending today (or yesterday while today has none yet).</summary>
    public int Streak { get; init; }

    /// <summary>Whether the streak reached the retention limit and may be longer.</summary>
    public bool StreakCapped { get; init; }

    /// <summary>Focus seconds per day for the last 14 days, oldest first (the last value is today).</summary>
    public IReadOnlyList<int> DailySeconds14 { get; init; } = [];

    /// <summary>Most blocked apps of the last 30 days (at most 6).</summary>
    public IReadOnlyList<AppCount> TopApps { get; init; } = [];

    /// <summary>Completed sessions of the last 30 days.</summary>
    public int OutcomeCompleted { get; init; }

    /// <summary>Sessions ended early with break-glass in the last 30 days.</summary>
    public int OutcomeEndedEarly { get; init; }

    /// <summary>Sessions with another end reason in the last 30 days.</summary>
    public int OutcomeOther { get; init; }

    /// <summary>Emergency passes used in the last 30 days.</summary>
    public int Passes30d { get; init; }

    /// <summary>Blocked attempts of the last 30 days.</summary>
    public int Blocked30d { get; init; }

    /// <summary>Blocked attempts of the last 30 days: 7 rows (Monday first) by 12 two-hour columns.</summary>
    public IReadOnlyList<IReadOnlyList<double>> Heatmap { get; init; } = [];

    /// <summary>Latest sessions, newest first (at most 8).</summary>
    public IReadOnlyList<SessionRecord> Recent { get; init; } = [];

    /// <summary>Sessions stored (all retained days).</summary>
    public int StoredSessions { get; init; }

    /// <summary>Blocked attempts stored (all retained days).</summary>
    public int StoredBlocked { get; init; }
}

/// <summary>Pure functions that turn day records into an <see cref="InsightsView"/>.</summary>
public static class InsightsAggregator
{
    private const int TopAppCount = 6;
    private const int RecentCount = 8;

    /// <summary>Combines day records (any order) for the local day <paramref name="today"/>.</summary>
    public static InsightsView Build(IReadOnlyList<DayRecord> days, IReadOnlyList<SessionRecord> sessions, int today)
    {
        int[] daily = new int[14];
        double[][] heat = [.. Enumerable.Range(0, 7).Select(_ => new double[InsightsLimits.HourBuckets])];
        Dictionary<string, int> apps = new(StringComparer.Ordinal);
        HashSet<int> completedDays = [];
        int focus7 = 0, focusToday = 0, completed7 = 0, blocked7 = 0;
        int completed30 = 0, early30 = 0, other30 = 0, passes30 = 0, blocked30 = 0;
        int storedSessions = 0, storedBlocked = 0;

        foreach (DayRecord day in days)
        {
            storedSessions += day.Completed + day.BreakGlass + day.OtherEnds;
            storedBlocked += day.Blocked;
            int age = today - day.Day;
            if (age < 0)
            {
                continue;
            }

            if (day.Completed > 0)
            {
                completedDays.Add(day.Day);
            }

            if (age < 14)
            {
                daily[13 - age] += day.FocusSeconds;
            }

            if (age == 0)
            {
                focusToday = day.FocusSeconds;
            }

            if (age < 7)
            {
                focus7 += day.FocusSeconds;
                completed7 += day.Completed;
                blocked7 += day.Blocked;
            }

            if (age < 30)
            {
                completed30 += day.Completed;
                early30 += day.BreakGlass;
                other30 += day.OtherEnds;
                passes30 += day.Passes;
                blocked30 += day.Blocked;
                AddHeat(heat, day);
                if (day.Apps is not null)
                {
                    foreach ((string name, int count) in day.Apps)
                    {
                        apps[name] = apps.GetValueOrDefault(name) + count;
                    }
                }
            }
        }

        int streak = 0;
        int cursor = completedDays.Contains(today) ? today : today - 1;
        while (completedDays.Contains(cursor))
        {
            streak++;
            cursor--;
        }

        List<SessionRecord> recent = [];
        for (int i = sessions.Count - 1; i >= 0 && recent.Count < RecentCount; i--)
        {
            recent.Add(sessions[i]);
        }

        return new InsightsView
        {
            Today = today,
            HasData = days.Count > 0 || sessions.Count > 0,
            FocusSeconds7d = focus7,
            FocusSecondsToday = focusToday,
            Completed7d = completed7,
            Blocked7d = blocked7,
            Streak = streak,
            StreakCapped = streak >= InsightsLimits.RetentionDays,
            DailySeconds14 = daily,
            TopApps = [.. apps.OrderByDescending(a => a.Value).ThenBy(a => a.Key, StringComparer.Ordinal).Take(TopAppCount).Select(a => new AppCount(a.Key, a.Value))],
            OutcomeCompleted = completed30,
            OutcomeEndedEarly = early30,
            OutcomeOther = other30,
            Passes30d = passes30,
            Blocked30d = blocked30,
            Heatmap = heat,
            Recent = recent,
            StoredSessions = storedSessions,
            StoredBlocked = storedBlocked,
        };
    }

    /// <summary>Row of the heatmap for a local day number: Monday is 0, Sunday is 6.</summary>
    public static int WeekdayRow(int dayNumber) => ((int)DateOnly.FromDayNumber(dayNumber).DayOfWeek + 6) % 7;

    private static void AddHeat(double[][] heat, DayRecord day)
    {
        if (day.Hours is not { Length: InsightsLimits.HourBuckets } hours)
        {
            return;
        }

        double[] row = heat[WeekdayRow(day.Day)];
        for (int i = 0; i < hours.Length; i++)
        {
            row[i] += hours[i];
        }
    }
}

/// <summary>Deterministic example data for the preview. It is generated on the fly and never stored.</summary>
public static class InsightsSampleData
{
    private const ulong Seed = 0x2545F4914F6CDD1DUL;
    private const int Days = 45;

    private static readonly int[] PlannedMinutes = [25, 30, 45, 60, 90];
    private static readonly string[] Apps = ["discord.exe", "steam.exe", "chrome.exe", "spotify.exe", "telegram.exe", "epicgameslauncher.exe", "slack.exe"];

    /// <summary>Builds about six weeks of plausible history ending today, always with a current streak.</summary>
    public static InsightsView Create(int today, TimeZoneInfo zone)
    {
        var rng = new Rng(Seed);
        List<DayRecord> days = [];
        List<SessionRecord> sessions = [];
        for (int age = Days - 1; age >= 0; age--)
        {
            int dayNumber = today - age;
            DateOnly date = DateOnly.FromDayNumber(dayNumber);
            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            int count = age <= 5 ? 1 + rng.Next(3) : rng.Next(100) < (weekend ? 35 : 80) ? 1 + rng.Next(3) : 0;
            var day = new DayRecord { Day = dayNumber };
            for (int i = 0; i < count; i++)
            {
                SessionRecord session = NewSession(rng, date, i, forceCompleted: age <= 5 && i == 0, zone);
                sessions.Add(session);
                day.FocusSeconds += session.Actual;
                switch (session.Reason)
                {
                    case SessionOutcome.Completed:
                        day.Completed++;
                        break;
                    case SessionOutcome.EndedEarly:
                        day.BreakGlass++;
                        break;
                    default:
                        day.OtherEnds++;
                        break;
                }
            }

            AddBlocked(rng, day, count > 0 ? 2 + rng.Next(14) : rng.Next(3));
            day.Passes = rng.Next(100) < 10 ? 1 : 0;
            days.Add(day);
        }

        return InsightsAggregator.Build(days, sessions.TakeLast(InsightsLimits.MaxRecentSessions).ToList(), today);
    }

    private static SessionRecord NewSession(Rng rng, DateOnly date, int index, bool forceCompleted, TimeZoneInfo zone)
    {
        int planned = PlannedMinutes[rng.Next(PlannedMinutes.Length)] * 60;
        int roll = rng.Next(100);
        string reason = forceCompleted || roll < 84 ? SessionOutcome.Completed : roll < 94 ? SessionOutcome.EndedEarly : SessionOutcome.Other;
        int actual = reason switch
        {
            SessionOutcome.Completed => planned,
            SessionOutcome.EndedEarly => planned * (20 + rng.Next(60)) / 100,
            _ => planned * (10 + rng.Next(80)) / 100,
        };
        DateTime local = date.ToDateTime(new TimeOnly(8 + (index * 4) + rng.Next(3), rng.Next(60)));
        long start = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeSeconds();
        return new SessionRecord { Start = start, Planned = planned, Actual = actual, Reason = reason };
    }

    private static void AddBlocked(Rng rng, DayRecord day, int attempts)
    {
        if (attempts == 0)
        {
            return;
        }

        day.Blocked = attempts;
        day.Hours = new int[InsightsLimits.HourBuckets];
        day.Apps = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < attempts; i++)
        {
            string app = Apps[Math.Min(rng.Next(Apps.Length), rng.Next(Apps.Length))];
            day.Apps[app] = day.Apps.GetValueOrDefault(app) + 1;
            day.Hours[rng.Next(10) < 2 ? rng.Next(InsightsLimits.HourBuckets) : 4 + rng.Next(7)]++;
        }
    }

    // xorshift64: tiny and identical on every runtime, so the example never changes between runs.
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int maxExclusive)
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return (int)(_state % (ulong)maxExclusive);
        }
    }
}

/// <summary>Text formatting shared by the page, the widget and the settings section.</summary>
public static class InsightsFormat
{
    /// <summary>"45 min" or "1 h 05 min".</summary>
    public static string Minutes(int minutes) => minutes >= 60
        ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:00} min")
        : string.Create(CultureInfo.InvariantCulture, $"{minutes} min");

    /// <summary>Like <see cref="Minutes"/> but for seconds; anything under a minute reads "&lt;1 min".</summary>
    public static string Duration(int seconds) => seconds is > 0 and < 60 ? "<1 min" : Minutes(seconds / 60);

    /// <summary>"1 session", "2 sessions".</summary>
    public static string Count(int count, string singular) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {singular}{(count == 1 ? string.Empty : "s")}");

    /// <summary>"Nov 5".</summary>
    public static string DayLabel(int dayNumber) => DateOnly.FromDayNumber(dayNumber).ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>"Mon".</summary>
    public static string WeekdayLabel(int dayNumber) => DateOnly.FromDayNumber(dayNumber).ToString("ddd", CultureInfo.InvariantCulture);

    /// <summary>"Nov 5, 14:30" in the given time zone.</summary>
    public static string StartLabel(long unixSeconds, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone).ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>User-facing outcome name.</summary>
    public static string OutcomeLabel(string code) => code switch
    {
        SessionOutcome.Completed => "Completed",
        SessionOutcome.EndedEarly => "Ended early",
        _ => "Other",
    };
}
