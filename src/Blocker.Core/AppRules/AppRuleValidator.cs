using System.Globalization;
using Blocker.Contracts;
using Blocker.Core.Policy;
using Blocker.Core.ServiceHost;

namespace Blocker.Core.AppRules;

/// <summary>Result of <see cref="AppRuleValidator.Validate"/>.</summary>
/// <param name="Rules">The normalized rules; <see langword="null"/> when there are errors.</param>
/// <param name="Errors">User-readable errors; empty when valid.</param>
public sealed record AppRulesValidation(AppRulesDto? Rules, IReadOnlyList<string> Errors)
{
    /// <summary>Gets whether the rules passed validation.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates the rules a client sends and brings them into the one form the rest of the service relies on.</summary>
public static class AppRuleValidator
{
    /// <summary>Largest number of rules.</summary>
    public const int MaxRules = 200;

    /// <summary>Largest number of blocked ranges per rule, counted as sent (before touching ranges are merged).</summary>
    public const int MaxRangesPerRule = 50;

    /// <summary>Largest number of errors returned; the rest is not worth reading.</summary>
    public const int MaxErrors = 20;

    /// <summary>Minutes in a day.</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Minutes in a week.</summary>
    public const int MinutesPerWeek = 7 * MinutesPerDay;

    private const int MaxLabelLength = 64;

    /// <summary>
    /// Checks every rule and returns them normalized: the app entry trimmed with a lower-case hash, blocked ranges sorted and merged per day
    /// (overlapping and touching ranges become one; ranges of neighbouring days stay separate), and only the fields of the chosen pool mode kept.
    /// </summary>
    /// <param name="rules">The rules as sent.</param>
    /// <param name="windowsDirectory"><c>%SystemRoot%</c>: a program that runs from it cannot get a rule (the enforcer never touches it).</param>
    /// <returns>The normalized rules or the errors.</returns>
    public static AppRulesValidation Validate(AppRulesDto rules, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        if (rules.Rules is null)
        {
            return new AppRulesValidation(null, ["The rules are missing."]);
        }

        if (rules.Rules.Count > MaxRules)
        {
            return new AppRulesValidation(null, [$"At most {MaxRules} app rules are allowed."]);
        }

        List<string> errors = [];
        List<AppRuleDto> normalized = [];
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        foreach (AppRuleDto rule in rules.Rules)
        {
            // The wire format rejects nulls, but a rule built in code or read from a file must not crash the service.
            if (rule?.App is null || rule.BlockedRanges is null || rule.BlockedRanges.Any(static r => r is null) || rule.Budget is null)
            {
                errors.Add("A rule is incomplete.");
                continue;
            }

            string label = Label(rule.App);
            int errorsBefore = errors.Count;
            AllowlistEntryDto app = NormalizeApp(rule.App, errors);
            if (errors.Count == errorsBefore && WinPath.IsUnder(WinPath.Normalize(app.ExecutablePath), WinPath.Normalize(windowsDirectory)))
            {
                errors.Add($"'{label}' runs from the Windows directory ({Printable(windowsDirectory)}), whose programs Blocker never closes, so it cannot get a rule.");
            }

            IReadOnlyList<WeeklyRangeDto> ranges = NormalizeRanges(label, rule.BlockedRanges, errors);
            AppBudgetDto budget = NormalizeBudget(label, rule.Budget, errors);
            if (errors.Count > errorsBefore)
            {
                continue;
            }

            foreach (string key in AppRuleIdentity.Keys(app))
            {
                if (!owners.TryAdd(key, label))
                {
                    errors.Add($"'{label}' appears in more than one rule ('{owners[key]}').");
                    break;
                }
            }

            normalized.Add(new AppRuleDto(app, ranges, budget));
        }

        return errors.Count == 0
            ? new AppRulesValidation(new AppRulesDto(normalized), [])
            : new AppRulesValidation(null, [.. errors.Take(MaxErrors).Select(Printable)]);
    }

    private static AllowlistEntryDto NormalizeApp(AllowlistEntryDto app, List<string> errors)
    {
        errors.AddRange(AllowlistMapping.Validate([app], []));
        return new AllowlistEntryDto(
            app.DisplayName?.Trim() ?? string.Empty,
            NullIfBlank(app.ExecutablePath),
            NullIfBlank(app.Sha256)?.ToLowerInvariant(),
            NullIfBlank(app.PackageFamilyName));
    }

    private static List<WeeklyRangeDto> NormalizeRanges(string label, IReadOnlyList<WeeklyRangeDto> ranges, List<string> errors)
    {
        if (ranges.Count > MaxRangesPerRule)
        {
            errors.Add($"'{label}' has more than {MaxRangesPerRule} blocked ranges.");
            return [];
        }

        if (ranges.FirstOrDefault(static r => r.Day is < 0 or > 6 || r.StartMinute < 0 || r.EndMinute > MinutesPerDay || r.StartMinute >= r.EndMinute) is { } bad)
        {
            errors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Blocked range of '{label}' (day {bad.Day}, minutes {bad.StartMinute}-{bad.EndMinute}) is invalid: the day is 0 (Monday) to 6 (Sunday) and the range must start before it ends, within 0 to {MinutesPerDay} minutes."));
            return [];
        }

        List<WeeklyRangeDto> merged = [];
        foreach (IGrouping<int, WeeklyRangeDto> day in ranges.GroupBy(static r => r.Day).OrderBy(static g => g.Key))
        {
            WeeklyRangeDto? current = null;
            foreach (WeeklyRangeDto next in day.OrderBy(static r => r.StartMinute))
            {
                if (current is { } open && next.StartMinute <= open.EndMinute)
                {
                    current = open with { EndMinute = Math.Max(open.EndMinute, next.EndMinute) };
                    continue;
                }

                if (current is { } finished)
                {
                    merged.Add(finished);
                }

                current = next;
            }

            merged.Add(current!);
        }

        return merged;
    }

    private static AppBudgetDto NormalizeBudget(string label, AppBudgetDto budget, List<string> errors)
    {
        const int days = 7;
        switch (budget.Mode)
        {
            case AppBudgetMode.None:
                return new AppBudgetDto(AppBudgetMode.None, 0, null, 0);
            case AppBudgetMode.Daily when budget.DailyMinutes is >= 0 and <= MinutesPerDay:
                return new AppBudgetDto(AppBudgetMode.Daily, budget.DailyMinutes, null, 0);
            case AppBudgetMode.Weekly when budget.WeeklyMinutes is >= 0 and <= MinutesPerWeek:
                return new AppBudgetDto(AppBudgetMode.Weekly, 0, null, budget.WeeklyMinutes);
            case AppBudgetMode.PerWeekday when budget.PerWeekdayMinutes is { Count: days } perDay && perDay.All(static m => m is >= 0 and <= MinutesPerDay):
                return new AppBudgetDto(AppBudgetMode.PerWeekday, 0, [.. perDay], 0);
            case AppBudgetMode.Daily:
                errors.Add($"The daily time limit of '{label}' must be 0 to {MinutesPerDay} minutes.");
                break;
            case AppBudgetMode.Weekly:
                errors.Add($"The weekly time limit of '{label}' must be 0 to {MinutesPerWeek} minutes.");
                break;
            case AppBudgetMode.PerWeekday:
                errors.Add($"The per-weekday time limit of '{label}' needs {days} values (Monday to Sunday), each 0 to {MinutesPerDay} minutes.");
                break;
            default:
                errors.Add($"The time limit mode of '{label}' is unknown.");
                break;
        }

        return new AppBudgetDto(AppBudgetMode.None, 0, null, 0);
    }

    /// <summary>The name of an app as shown in messages: trimmed, without control characters, bounded.</summary>
    internal static string Label(AllowlistEntryDto app)
    {
        string name = Printable(app.DisplayName?.Trim() ?? string.Empty);
        if (name.Length == 0)
        {
            return "unnamed app";
        }

        return name.Length <= MaxLabelLength ? name : name[..MaxLabelLength] + "...";
    }

    private static string Printable(string text) => string.Concat(text.Select(static c => char.IsControl(c) ? '?' : c));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
