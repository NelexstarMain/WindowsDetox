using Blocker.Contracts;
using Blocker.Core.AppRules;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleValidatorTests
{
    private const string WindowsDirectory = @"C:\Windows";

    private static AppRulesValidation Validate(params AppRuleDto[] rules) => AppRuleValidator.Validate(new AppRulesDto(rules), WindowsDirectory);

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"c:/windows/explorer.exe")]
    [InlineData(@"\\?\C:\WINDOWS\System32\calc.exe")]
    [InlineData(@"C:\Games\..\Windows\System32\mspaint.exe")]
    [InlineData(@"C:\Windows\Tasks\game.exe")]
    public void A_program_in_the_windows_directory_cannot_get_a_rule(string path)
    {
        AppRulesValidation result = Validate(RuleFor(new AllowlistEntryDto("Notepad", path, null, null), Daily(0)));

        Assert.False(result.IsValid);
        Assert.Null(result.Rules);
        string error = Assert.Single(result.Errors);
        Assert.Contains("Notepad", error, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Windows.old\game.exe")]
    [InlineData(@"C:\Windowsx\game.exe")]
    [InlineData(@"C:\Games\Windows\game.exe")]
    [InlineData(@"D:\Windows\game.exe")]
    public void A_path_that_only_looks_like_the_windows_directory_is_fine(string path)
    {
        Assert.True(Validate(RuleFor(new AllowlistEntryDto("Game", path, null, null), Daily(0))).IsValid);
    }

    [Fact]
    public void A_rule_without_a_path_is_not_affected_by_the_windows_directory()
    {
        var byHash = new AllowlistEntryDto("Game", null, new string('a', 64), null);
        var byPackage = new AllowlistEntryDto("Store game", null, null, "Pkg.Game_abc");

        Assert.True(Validate(RuleFor(byHash, Daily(0)), RuleFor(byPackage, Daily(0))).IsValid);
    }

    [Fact]
    public void An_empty_rule_set_is_valid()
    {
        AppRulesValidation result = Validate();

        Assert.True(result.IsValid);
        Assert.Empty(result.Rules!.Rules);
    }

    [Fact]
    public void A_valid_rule_is_normalized_the_way_the_allowlist_reads_it()
    {
        var app = new AllowlistEntryDto("  Game  ", " C:\\Games\\game.exe ", "  ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789 ", "   ");

        AppRulesValidation result = Validate(RuleFor(app, Daily(30)));

        Assert.True(result.IsValid);
        AppRuleDto rule = Assert.Single(result.Rules!.Rules);
        Assert.Equal("Game", rule.App.DisplayName);
        Assert.Equal(@"C:\Games\game.exe", rule.App.ExecutablePath);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", rule.App.Sha256);
        Assert.Null(rule.App.PackageFamilyName);
    }

    [Theory]
    [InlineData(-1, 0, 60)]
    [InlineData(7, 0, 60)]
    [InlineData(0, -1, 60)]
    [InlineData(0, 60, 60)]
    [InlineData(0, 90, 60)]
    [InlineData(0, 0, 1441)]
    [InlineData(0, 1440, 1440)]
    public void A_range_outside_the_week_or_without_length_is_rejected(int day, int start, int end)
    {
        AppRulesValidation result = Validate(Rule(null, Range(day, start, end)));

        Assert.False(result.IsValid);
        Assert.Null(result.Rules);
        Assert.Contains(result.Errors, e => e.Contains("Game", StringComparison.Ordinal));
    }

    [Fact]
    public void Ranges_are_merged_per_day_when_they_overlap_or_touch_and_sorted()
    {
        AppRulesValidation result = Validate(Rule(null, Range(2, 600, 700), Range(0, 60, 120), Range(0, 100, 200), Range(0, 200, 300), Range(0, 400, 500), Range(2, 0, 30)));

        Assert.True(result.IsValid);
        Assert.Equal(
            [new WeeklyRangeDto(0, 60, 300), new WeeklyRangeDto(0, 400, 500), new WeeklyRangeDto(2, 0, 30), new WeeklyRangeDto(2, 600, 700)],
            result.Rules!.Rules[0].BlockedRanges);
    }

    [Fact]
    public void Ranges_of_neighbouring_days_are_not_merged()
    {
        AppRulesValidation result = Validate(Rule(null, Range(0, 1380, 1440), Range(1, 0, 60)));

        Assert.Equal([new WeeklyRangeDto(0, 1380, 1440), new WeeklyRangeDto(1, 0, 60)], result.Rules!.Rules[0].BlockedRanges);
    }

    [Fact]
    public void At_most_fifty_ranges_per_rule_and_two_hundred_rules_are_accepted()
    {
        WeeklyRangeDto[] fifty = [.. Enumerable.Range(0, 50).Select(i => Range(i % 7, (i / 7) * 100, ((i / 7) * 100) + 10))];
        WeeklyRangeDto[] fiftyOne = [.. fifty, Range(6, 1000, 1010)];
        AppRuleDto[] manyRules = [.. Enumerable.Range(0, 201).Select(i => RuleFor(new AllowlistEntryDto($"App {i}", $@"C:\Apps\app{i}.exe", null, null)))];

        Assert.True(Validate(Rule(null, fifty)).IsValid);
        Assert.False(Validate(Rule(null, fiftyOne)).IsValid);
        Assert.True(Validate(manyRules[..200]).IsValid);
        Assert.False(Validate(manyRules).IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void A_daily_pool_must_fit_into_a_day(int minutes) => Assert.False(Validate(Rule(Daily(minutes))).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(1440)]
    public void The_edges_of_a_daily_pool_are_valid(int minutes) => Assert.True(Validate(Rule(Daily(minutes))).IsValid);

    [Theory]
    [InlineData(-1)]
    [InlineData(10081)]
    public void A_weekly_pool_must_fit_into_a_week(int minutes) => Assert.False(Validate(Rule(Weekly(minutes))).IsValid);

    [Fact]
    public void A_weekly_pool_of_a_whole_week_is_valid() => Assert.True(Validate(Rule(Weekly(10080))).IsValid);

    [Fact]
    public void A_per_weekday_pool_needs_exactly_seven_values_inside_a_day()
    {
        Assert.True(Validate(Rule(PerWeekday(0, 10, 20, 30, 40, 50, 1440))).IsValid);
        Assert.False(Validate(Rule(PerWeekday(0, 10, 20, 30, 40, 50))).IsValid);
        Assert.False(Validate(Rule(PerWeekday(0, 10, 20, 30, 40, 50, 60, 70))).IsValid);
        Assert.False(Validate(Rule(PerWeekday(0, 10, 20, 30, 40, 50, 1441))).IsValid);
        Assert.False(Validate(Rule(PerWeekday(0, 10, 20, 30, 40, 50, -5))).IsValid);
        Assert.False(Validate(Rule(new AppBudgetDto(AppBudgetMode.PerWeekday, 0, null, 0))).IsValid);
    }

    [Fact]
    public void Only_the_fields_of_the_chosen_pool_mode_are_kept()
    {
        AppRulesValidation daily = Validate(Rule(new AppBudgetDto(AppBudgetMode.Daily, 30, [1, 2, 3, 4, 5, 6, 7], 999)));
        AppRulesValidation none = Validate(Rule(new AppBudgetDto(AppBudgetMode.None, 30, [1, 2, 3, 4, 5, 6, 7], 999)));

        AppBudgetDto dailyBudget = daily.Rules!.Rules[0].Budget;
        Assert.Equal((AppBudgetMode.Daily, 30, 0), (dailyBudget.Mode, dailyBudget.DailyMinutes, dailyBudget.WeeklyMinutes));
        Assert.Null(dailyBudget.PerWeekdayMinutes);
        AppBudgetDto noneBudget = none.Rules!.Rules[0].Budget;
        Assert.Equal((AppBudgetMode.None, 0, 0), (noneBudget.Mode, noneBudget.DailyMinutes, noneBudget.WeeklyMinutes));
        Assert.Null(noneBudget.PerWeekdayMinutes);
    }

    [Fact]
    public void An_unknown_pool_mode_is_rejected() =>
        Assert.False(Validate(Rule(new AppBudgetDto((AppBudgetMode)42, 0, null, 0))).IsValid);

    [Fact]
    public void An_app_needs_a_usable_identity()
    {
        Assert.False(Validate(RuleFor(new AllowlistEntryDto("Ghost", null, null, null))).IsValid);
        Assert.False(Validate(RuleFor(new AllowlistEntryDto("Tool", null, "abc", null))).IsValid);
        Assert.False(Validate(RuleFor(new AllowlistEntryDto("", GamePath, null, null))).IsValid);
    }

    [Fact]
    public void Two_rules_for_the_same_path_are_a_duplicate_however_the_path_is_written()
    {
        AppRulesValidation result = Validate(
            Rule(),
            RuleFor(new AllowlistEntryDto("Game again", @"c:/games/./GAME.EXE", null, null)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Game again", StringComparison.Ordinal) && e.Contains("more than one", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_rules_that_share_a_hash_or_a_package_are_a_duplicate()
    {
        string hash = new('a', 64);

        Assert.False(Validate(
            RuleFor(new AllowlistEntryDto("A", @"C:\a.exe", hash, null)),
            RuleFor(new AllowlistEntryDto("B", @"C:\b.exe", hash.ToUpperInvariant(), null))).IsValid);
        Assert.False(Validate(
            RuleFor(new AllowlistEntryDto("A", @"C:\a.exe", null, "Pkg.Family_abc")),
            RuleFor(new AllowlistEntryDto("B", null, null, "pkg.family_ABC"))).IsValid);
    }

    [Fact]
    public void A_hash_decides_alone_so_a_path_only_rule_and_a_hash_rule_for_one_file_are_distinct_identities()
    {
        AppRulesValidation result = Validate(
            RuleFor(new AllowlistEntryDto("By path", GamePath, null, null)),
            RuleFor(new AllowlistEntryDto("By hash", GamePath, new string('b', 64), null)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_rule_with_missing_parts_is_rejected_instead_of_crashing()
    {
        Assert.False(Validate(new AppRuleDto(null!, [], NoBudget)).IsValid);
        Assert.False(Validate(new AppRuleDto(Game, null!, NoBudget)).IsValid);
        Assert.False(Validate(new AppRuleDto(Game, [null!], NoBudget)).IsValid);
        Assert.False(Validate(new AppRuleDto(Game, [], null!)).IsValid);
        Assert.False(Validate(null!).IsValid);
    }

    [Fact]
    public void Errors_are_capped_and_never_echo_control_characters()
    {
        AppRuleDto[] bad = [.. Enumerable.Range(0, 100).Select(i => RuleFor(new AllowlistEntryDto($"App\n{i}", $@"C:\Apps\{i}.exe", null, null), null, Range(9, 0, 10)))];

        AppRulesValidation result = Validate(bad);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count <= AppRuleValidator.MaxErrors);
        Assert.DoesNotContain(result.Errors, e => e.Contains('\n', StringComparison.Ordinal));
    }
}
