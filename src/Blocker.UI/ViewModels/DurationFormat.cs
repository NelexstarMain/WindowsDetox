using System.Globalization;

namespace Blocker.UI.ViewModels;

/// <summary>Formats durations for the countdown clock and for summaries.</summary>
internal static class DurationFormat
{
    /// <summary>Text shown when no time is available.</summary>
    public const string NoClock = "--:--:--";

    /// <summary>
    /// Formats a countdown as <c>HH:MM:SS</c>. Hours are not wrapped at 24 (two days show as <c>48:00:00</c>), partial seconds round up
    /// so the clock reads <c>00:00:00</c> only when time is really up, and negative values clamp to zero.
    /// </summary>
    /// <param name="value">Remaining time, or <see langword="null"/>.</param>
    /// <returns>The clock text.</returns>
    public static string Clock(TimeSpan? value)
    {
        if (value is not { } span)
        {
            return NoClock;
        }

        long seconds = (long)Math.Ceiling(Math.Max(0, span.TotalSeconds));
        long hours = seconds / 3600;
        long minutes = seconds % 3600 / 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds % 60:00}");
    }

    /// <summary>Formats a length such as <c>45 min</c>, <c>2 h</c>, <c>1 h 30 min</c> or <c>2 days</c>.</summary>
    /// <param name="value">Length to format.</param>
    /// <returns>Compact text.</returns>
    public static string Compact(TimeSpan value)
    {
        long minutes = (long)Math.Round(Math.Max(0, value.TotalMinutes));
        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            long days = minutes / 1440;
            return string.Create(CultureInfo.InvariantCulture, $"{days} {(days == 1 ? "day" : "days")}");
        }

        long hours = minutes / 60;
        long rest = minutes % 60;
        return (hours, rest) switch
        {
            (0, _) => string.Create(CultureInfo.InvariantCulture, $"{rest} min"),
            (_, 0) => string.Create(CultureInfo.InvariantCulture, $"{hours} h"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{hours} h {rest} min"),
        };
    }
}
