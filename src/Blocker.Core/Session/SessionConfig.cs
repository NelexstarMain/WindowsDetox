namespace Blocker.Core.Session;

/// <summary>Immutable configuration chosen when a session starts.</summary>
/// <param name="Duration">Planned session length.</param>
/// <param name="EmergencyPassesPerDay">Passes granted per local day (in <paramref name="TimeZoneId"/>).</param>
/// <param name="EmergencyPassDuration">Length of one pass.</param>
/// <param name="CarryOverUnusedPasses">Whether unused passes accumulate (capped at 3x the daily amount).</param>
/// <param name="TimeZoneId">Time zone frozen at session start; defines when the "day" rolls over.</param>
public sealed record SessionConfig(
    TimeSpan Duration,
    int EmergencyPassesPerDay,
    TimeSpan EmergencyPassDuration,
    bool CarryOverUnusedPasses,
    string TimeZoneId);

/// <summary>Validates <see cref="SessionConfig"/> values.</summary>
public static class SessionConfigValidator
{
    /// <summary>Shortest allowed session.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);

    /// <summary>Longest allowed session.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(30);

    /// <summary>Shortest allowed emergency pass.</summary>
    public static readonly TimeSpan MinPassDuration = TimeSpan.FromMinutes(1);

    /// <summary>Longest allowed emergency pass.</summary>
    public static readonly TimeSpan MaxPassDuration = TimeSpan.FromMinutes(30);

    /// <summary>Maximum passes per day.</summary>
    public const int MaxPassesPerDay = 10;

    /// <summary>Returns human-readable errors; empty when <paramref name="config"/> is valid.</summary>
    public static IReadOnlyList<string> Validate(SessionConfig config)
    {
        List<string> errors = [];
        if (config.Duration < MinDuration || config.Duration > MaxDuration)
        {
            errors.Add($"Duration must be between {MinDuration.TotalMinutes:0} minutes and {MaxDuration.TotalDays:0} days.");
        }

        if (config.EmergencyPassesPerDay is < 0 or > MaxPassesPerDay)
        {
            errors.Add($"Emergency passes per day must be between 0 and {MaxPassesPerDay}.");
        }

        if (config.EmergencyPassDuration < MinPassDuration || config.EmergencyPassDuration > MaxPassDuration)
        {
            errors.Add($"Emergency pass duration must be between {MinPassDuration.TotalMinutes:0} and {MaxPassDuration.TotalMinutes:0} minutes.");
        }

        if (TryResolveZone(config.TimeZoneId) is null)
        {
            errors.Add($"Time zone '{config.TimeZoneId}' is not available on this system.");
        }

        return errors;
    }

    /// <summary>Resolves a time zone id, or <see langword="null"/> when unknown.</summary>
    internal static TimeZoneInfo? TryResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
