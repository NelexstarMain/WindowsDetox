using Blocker.Contracts;

namespace Blocker.Core.Tests.AppRules;

/// <summary>Builders for rules and the Monday-based calendar the rule tests share.</summary>
internal static class AppRuleTestData
{
    public const string GamePath = @"C:\Games\game.exe";
    public const string ChatPath = @"C:\Apps\chat.exe";

    public static readonly AppBudgetDto NoBudget = new(AppBudgetMode.None, 0, null, 0);

    /// <summary>Monday 2026-11-23 00:00; <see cref="At"/> counts days from it.</summary>
    public static readonly DateTime Monday = new(2026, 11, 23);

    public static AllowlistEntryDto Game { get; } = new("Game", GamePath, null, null);

    public static AppBudgetDto Daily(int minutes) => new(AppBudgetMode.Daily, minutes, null, 0);

    public static AppBudgetDto Weekly(int minutes) => new(AppBudgetMode.Weekly, 0, null, minutes);

    public static AppBudgetDto PerWeekday(params int[] minutes) => new(AppBudgetMode.PerWeekday, 0, minutes, 0);

    public static WeeklyRangeDto Range(int day, int startMinute, int endMinute) => new(day, startMinute, endMinute);

    public static AppRuleDto Rule(AppBudgetDto? budget = null, params WeeklyRangeDto[] ranges) => new(Game, ranges, budget ?? NoBudget);

    public static AppRuleDto RuleFor(AllowlistEntryDto app, AppBudgetDto? budget = null, params WeeklyRangeDto[] ranges) => new(app, ranges, budget ?? NoBudget);

    /// <summary>Local wall-clock time <paramref name="day"/> days (0 = Monday) after <see cref="Monday"/> at <paramref name="hour"/>:<paramref name="minute"/>.</summary>
    public static DateTime At(int day, int hour, int minute = 0, int second = 0) => Monday.AddDays(day).AddHours(hour).AddMinutes(minute).AddSeconds(second);
}
