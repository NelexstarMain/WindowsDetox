// Date and time helpers: day numbers, conversion between local wall-clock time and instants (daylight saving aware), parsing of
// user input (with clamping of impossible dates such as 31 February) and the English texts the plugin shows.

using System.Globalization;

namespace CalendarPlanner;

/// <summary>Pure date and time functions; every zone is passed in, nothing reads the system clock.</summary>
public static class CalTime
{
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] LongDayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>Gets the local day number of an instant.</summary>
    public static int DayOf(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime).DayNumber;

    /// <summary>Gets the local minute of the day (0-1439) of an instant.</summary>
    public static int MinuteOf(DateTimeOffset utc, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(utc, zone).DateTime;
        return (local.Hour * 60) + local.Minute;
    }

    /// <summary>Converts a local day and minute to an instant. A time inside a spring-forward gap moves to the first minute after the gap; a time that happens twice in autumn means the first time.</summary>
    public static DateTimeOffset ToUtc(int day, int minute, TimeZoneInfo zone)
    {
        DateTime local = DateOnly.FromDayNumber(day).ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Clamp(minute, 0, 1439));
        for (int i = 0; i < 180 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(1);
        }

        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Monday-based weekday of a day (0 = Monday ... 6 = Sunday).</summary>
    public static int Weekday(int day) => day % 7;

    /// <summary>First day of the week containing <paramref name="day"/>.</summary>
    public static int WeekStart(int day, bool sundayFirst) => day - ((Weekday(day) + (sundayFirst ? 1 : 0)) % 7);

    /// <summary>First day of the month containing <paramref name="day"/>.</summary>
    public static int MonthStart(int day)
    {
        DateOnly date = DateOnly.FromDayNumber(day);
        return new DateOnly(date.Year, date.Month, 1).DayNumber;
    }

    /// <summary>First day of the month <paramref name="months"/> after (or before) the month of <paramref name="day"/>, kept within the accepted years.</summary>
    public static int AddMonths(int day, int months)
    {
        DateOnly first = DateOnly.FromDayNumber(MonthStart(day)).AddMonths(months);
        int year = Math.Clamp(first.Year, CalendarLimits.MinYear, CalendarLimits.MaxYear);
        return year == first.Year ? first.DayNumber : new DateOnly(year, year == CalendarLimits.MinYear ? 1 : 12, 1).DayNumber;
    }

    /// <summary>Number of days in the month containing <paramref name="day"/>.</summary>
    public static int DaysInMonth(int day)
    {
        DateOnly date = DateOnly.FromDayNumber(day);
        return DateTime.DaysInMonth(date.Year, date.Month);
    }

    /// <summary>The 42 days (six weeks) of the month grid of the month containing <paramref name="day"/>.</summary>
    public static IReadOnlyList<int> MonthGrid(int day, bool sundayFirst)
    {
        int first = WeekStart(MonthStart(day), sundayFirst);
        return [.. Enumerable.Range(first, 42)];
    }

    /// <summary>Whether two days are in the same month.</summary>
    public static bool SameMonth(int a, int b) => MonthStart(a) == MonthStart(b);

    /// <summary>Parses <c>yyyy-mm-dd</c> (also with <c>/</c> or <c>.</c>). A day past the end of its month is clamped to the last day, so 2026-02-31 means 2026-02-28.</summary>
    public static bool TryParseDate(string? text, out int day)
    {
        day = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split(['-', '/', '.']);
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int year) || parts[0].Length != 4
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int dom)
            || year is < CalendarLimits.MinYear or > CalendarLimits.MaxYear
            || month is < 1 or > 12
            || dom < 1)
        {
            return false;
        }

        day = new DateOnly(year, month, Math.Min(dom, DateTime.DaysInMonth(year, month))).DayNumber;
        return true;
    }

    /// <summary>Parses a time such as <c>9:05</c>, <c>09:05</c>, <c>17:30</c> or <c>9:05 pm</c> into the minute of the day.</summary>
    public static bool TryParseTime(string? text, out int minute)
    {
        minute = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim().ToLowerInvariant();
        int offset = 0;
        bool meridiem = false;
        if (value.EndsWith("pm", StringComparison.Ordinal) || value.EndsWith("am", StringComparison.Ordinal))
        {
            meridiem = true;
            offset = value.EndsWith("pm", StringComparison.Ordinal) ? 12 : 0;
            value = value[..^2].Trim();
        }

        string[] parts = value.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int min)
            || parts[1].Length != 2
            || min > 59)
        {
            return false;
        }

        if (meridiem)
        {
            if (hour is < 1 or > 12)
            {
                return false;
            }

            hour = (hour % 12) + offset;
        }
        else if (hour > 23)
        {
            return false;
        }

        minute = (hour * 60) + min;
        return true;
    }

    /// <summary>Formats a date as <c>yyyy-mm-dd</c>.</summary>
    public static string IsoDate(int day)
    {
        DateOnly date = DateOnly.FromDayNumber(day);
        return string.Create(CultureInfo.InvariantCulture, $"{date.Year:0000}-{date.Month:00}-{date.Day:00}");
    }

    /// <summary>Formats a minute of the day as <c>09:30</c> or <c>9:30 AM</c>.</summary>
    public static string Clock(int minute, bool use24Hour)
    {
        int hour = minute / 60;
        int min = minute % 60;
        if (use24Hour)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{min:00}");
        }

        int shown = hour % 12 == 0 ? 12 : hour % 12;
        return string.Create(CultureInfo.InvariantCulture, $"{shown}:{min:00} {(hour < 12 ? "AM" : "PM")}");
    }

    /// <summary>Formats a length as <c>45 min</c>, <c>2 h</c> or <c>1 h 30 min</c>.</summary>
    public static string Length(int minutes)
    {
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
        }

        return minutes % 60 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min");
    }

    /// <summary>Short weekday name.</summary>
    public static string DayName(int day) => DayNames[Weekday(day)];

    /// <summary>Full weekday name.</summary>
    public static string LongDayName(int day) => LongDayNames[Weekday(day)];

    /// <summary>Weekday name by Monday-based index.</summary>
    public static string DayNameByIndex(int index) => DayNames[index];

    /// <summary>Month name and year, for example <c>March 2026</c>.</summary>
    public static string MonthTitle(int day)
    {
        DateOnly date = DateOnly.FromDayNumber(day);
        return string.Create(CultureInfo.InvariantCulture, $"{MonthNames[date.Month - 1]} {date.Year}");
    }

    /// <summary>Short date, for example <c>Mon 9 Mar</c>.</summary>
    public static string ShortDate(int day)
    {
        DateOnly date = DateOnly.FromDayNumber(day);
        return string.Create(CultureInfo.InvariantCulture, $"{DayName(day)} {date.Day} {MonthNames[date.Month - 1][..3]}");
    }

    /// <summary>Long date, for example <c>Monday 9 March</c>.</summary>
    public static string LongDate(int day)
    {
        DateOnly date = DateOnly.FromDayNumber(day);
        return string.Create(CultureInfo.InvariantCulture, $"{LongDayName(day)} {date.Day} {MonthNames[date.Month - 1]}");
    }

    /// <summary>Text for how far away something is: <c>now</c>, <c>42 min</c>, <c>3.5 h</c>, <c>2 d</c>. At most six characters.</summary>
    public static string Countdown(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (span < TimeSpan.FromMinutes(100))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min");
        }

        if (span < TimeSpan.FromHours(48))
        {
            double hours = Math.Round(span.TotalHours, 1, MidpointRounding.ToZero);
            return string.Create(CultureInfo.InvariantCulture, $"{hours:0.#} h");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays} d");
    }

    /// <summary>Resolves a time zone by its IANA or Windows id; <see langword="null"/> when neither exists.</summary>
    public static TimeZoneInfo? FindZone(params string[] ids)
    {
        foreach (string id in ids)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone))
            {
                return zone;
            }
        }

        return null;
    }
}
