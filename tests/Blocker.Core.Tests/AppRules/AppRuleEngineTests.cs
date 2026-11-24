using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.AppRules;
using Blocker.Core.Security;
using Blocker.Core.Tests.Security;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleEngineTests
{
    private static readonly string Root = MockUnixSupport.Path(@"C:\ProgramData\WindowsDetox");
    private static readonly string GameKey = AppRuleIdentity.LedgerKey(Game);

    private readonly MockFileSystem _fs = new();
    private readonly SecureFileStore _store;
    private DateTimeOffset _utc = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero); // Monday 10:00
    private string _zone = "UTC";

    public AppRuleEngineTests()
    {
        var protector = new FakeSecretProtector();
        _store = new SecureFileStore(_fs, protector, new HmacKeyStore(_fs, protector, _fs.Path.Combine(Root, "hmac.key")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AppRuleEngine NewEngine() => new(
        new AppRulesStore(_store, _fs.Path.Combine(Root, "rules.bin"), NullLogger<AppRulesStore>.Instance),
        new AppUsageStore(_store, _fs.Path.Combine(Root, "usage.bin"), NullLogger<AppUsageStore>.Instance),
        () => _utc,
        () => _zone,
        @"C:\Windows");

    private async Task<AppRuleEngine> LoadedEngineAsync()
    {
        AppRuleEngine engine = NewEngine();
        await engine.LoadAsync(Ct);
        return engine;
    }

    private static async Task<IReadOnlyList<string>> SetAsync(AppRuleEngine engine, params AppRuleDto[] rules) =>
        await engine.SetAsync(new AppRulesDto(rules), Ct);

    private void SetLocal(DateTime local) => _utc = new DateTimeOffset(local, TimeSpan.Zero);

    [Fact]
    public async Task Rules_are_stored_normalized_with_the_zone_of_that_moment_and_survive_a_restart()
    {
        _zone = "Europe/Warsaw";
        AppRuleEngine engine = await LoadedEngineAsync();

        IReadOnlyList<string> errors = await SetAsync(engine, Rule(Daily(30), Range(0, 600, 700), Range(0, 650, 800)));
        _zone = "Asia/Tokyo";
        AppRuleEngine restarted = await LoadedEngineAsync();

        Assert.Empty(errors);
        Assert.Equal("Europe/Warsaw", restarted.ZoneId);
        AppRuleDto rule = Assert.Single(restarted.Rules.Rules);
        Assert.Equal([new WeeklyRangeDto(0, 600, 800)], rule.BlockedRanges);
        Assert.Equal(30, rule.Budget.DailyMinutes);
        Assert.True(restarted.CanBlock);
    }

    [Fact]
    public async Task Invalid_rules_are_refused_and_change_nothing()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        _ = await SetAsync(engine, Rule(Daily(30)));

        IReadOnlyList<string> errors = await SetAsync(engine, Rule(Daily(5000)));

        Assert.NotEmpty(errors);
        Assert.Equal(30, Assert.Single(engine.Rules.Rules).Budget.DailyMinutes);
        Assert.Equal(30, Assert.Single((await LoadedEngineAsync()).Rules.Rules).Budget.DailyMinutes);
    }

    [Fact]
    public async Task A_rule_set_without_anything_that_can_block_does_not_ask_for_enforcement()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.False(engine.CanBlock);

        _ = await SetAsync(engine, Rule());

        Assert.False(engine.CanBlock);
        _ = await SetAsync(engine, Rule(Daily(1)));
        Assert.True(engine.CanBlock);
        _ = await SetAsync(engine, Rule(null, Range(0, 0, 10)));
        Assert.True(engine.CanBlock);
    }

    [Fact]
    public async Task While_an_app_is_inside_a_scheduled_block_its_rule_can_only_get_stricter()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(60), Range(0, 9 * 60, 17 * 60))));

        IReadOnlyList<string> removed = await SetAsync(engine);
        IReadOnlyList<string> shorter = await SetAsync(engine, Rule(Daily(60), Range(0, 9 * 60, 16 * 60)));
        IReadOnlyList<string> bigger = await SetAsync(engine, Rule(Daily(90), Range(0, 9 * 60, 17 * 60)));
        IReadOnlyList<string> noPool = await SetAsync(engine, Rule(null, Range(0, 9 * 60, 17 * 60)));
        IReadOnlyList<string> same = await SetAsync(engine, Rule(Daily(60), Range(0, 9 * 60, 17 * 60)));
        IReadOnlyList<string> tighter = await SetAsync(engine, Rule(Daily(30), Range(0, 8 * 60, 18 * 60), Range(1, 0, 60)));

        Assert.Contains(removed, static e => e.Contains("cannot be removed", StringComparison.Ordinal) && e.Contains("Game", StringComparison.Ordinal));
        Assert.Contains(shorter, static e => e.Contains("stricter", StringComparison.Ordinal));
        Assert.NotEmpty(bigger);
        Assert.NotEmpty(noPool);
        Assert.Empty(same);
        Assert.Empty(tighter);
        Assert.Equal(30, Assert.Single(engine.Rules.Rules).Budget.DailyMinutes);
    }

    [Fact]
    public async Task Outside_a_block_a_rule_can_be_loosened_or_removed_freely()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(60), Range(0, 12 * 60, 17 * 60))));

        Assert.Empty(await SetAsync(engine, Rule(Daily(90), Range(0, 13 * 60, 14 * 60))));
        Assert.Empty(await SetAsync(engine));
        Assert.Empty(engine.Rules.Rules);
    }

    [Fact]
    public async Task An_exhausted_pool_cannot_be_enlarged_replaced_or_removed_but_can_be_shrunk()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(60))));
        Assert.True(engine.AddUsage([GameKey], 3600));

        IReadOnlyList<string> bigger = await SetAsync(engine, Rule(Daily(61)));
        IReadOnlyList<string> weekly = await SetAsync(engine, Rule(Weekly(600)));
        IReadOnlyList<string> removed = await SetAsync(engine);
        IReadOnlyList<string> smaller = await SetAsync(engine, Rule(Daily(10)));

        Assert.NotEmpty(bigger);
        Assert.NotEmpty(weekly);
        Assert.NotEmpty(removed);
        Assert.Empty(smaller);
    }

    [Fact]
    public async Task Other_apps_stay_editable_while_one_app_is_blocked()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        var chat = new AllowlistEntryDto("Chat", ChatPath, null, null);
        Assert.Empty(await SetAsync(engine, Rule(null, Range(0, 0, 1440)), RuleFor(chat, Daily(10))));

        IReadOnlyList<string> errors = await SetAsync(engine, Rule(null, Range(0, 0, 1440)), RuleFor(chat, Daily(500)));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Widening_the_identity_of_a_blocked_app_carries_its_usage_over()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        var byPackage = new AllowlistEntryDto("Store game", null, null, "Pkg.Game_abc");
        Assert.Empty(await SetAsync(engine, RuleFor(byPackage, Daily(60))));
        Assert.True(engine.AddUsage([AppRuleIdentity.LedgerKey(byPackage)], 3600));

        // The new entry keeps the package and adds a path, so the app is still matched everywhere it was, and its ledger key moves to the path.
        IReadOnlyList<string> errors = await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", GamePath, null, "Pkg.Game_abc"), Daily(60)));

        Assert.Empty(errors);
        AppRuleStatusDto status = Assert.Single(engine.GetStatus(sessionRunning: false).Items);
        Assert.Equal(AppRuleState.BudgetExhausted, status.State);
        Assert.Equal(3600, status.UsedSeconds);
    }

    [Fact]
    public async Task A_blocked_app_cannot_move_its_rule_to_an_identity_that_only_shares_a_key_with_the_old_one()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        var original = new AllowlistEntryDto("Store game", GamePath, null, "Pkg.Game_abc");
        Assert.Empty(await SetAsync(engine, RuleFor(original, Daily(60))));
        Assert.True(engine.AddUsage([AppRuleIdentity.LedgerKey(original)], 3600));

        // Two saves would otherwise walk the rule to a path nothing runs from: each step shares one key with the previous entry.
        IReadOnlyList<string> toDeadPath = await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", @"C:\Gone\game.exe", null, "Pkg.Game_abc"), Daily(60)));
        IReadOnlyList<string> droppedPath = await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", null, null, "Pkg.Game_abc"), Daily(60)));
        IReadOnlyList<string> droppedPackage = await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", GamePath, null, null), Daily(60)));
        IReadOnlyList<string> otherPackage = await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", GamePath, null, "Pkg.Other_xyz"), Daily(60)));

        foreach (IReadOnlyList<string> errors in new[] { toDeadPath, droppedPath, droppedPackage, otherPackage })
        {
            string error = Assert.Single(errors);
            Assert.Contains("Store game", error, StringComparison.Ordinal);
            Assert.Contains("widen", error, StringComparison.Ordinal);
        }

        Assert.Equal(original, Assert.Single(engine.Rules.Rules).App);
        Assert.Equal(3600, Assert.Single(engine.GetStatus(sessionRunning: false).Items).UsedSeconds);
    }

    [Fact]
    public async Task A_blocked_app_cannot_swap_a_hash_for_another_hash_that_keeps_only_its_package()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        string hash = new('a', 64);
        Assert.Empty(await SetAsync(engine, RuleFor(new AllowlistEntryDto("Hashed", null, hash, "Pkg.Game_abc"), Daily(0))));

        IReadOnlyList<string> errors = await SetAsync(engine, RuleFor(new AllowlistEntryDto("Hashed", null, new string('b', 64), "Pkg.Game_abc"), Daily(0)));

        Assert.Contains(errors, static e => e.Contains("widen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_app_that_is_not_blocked_can_change_its_identity_freely()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", GamePath, null, "Pkg.Game_abc"), Daily(60))));

        Assert.Empty(await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", @"C:\Moved\game.exe", null, "Pkg.Game_abc"), Daily(60))));
        Assert.Empty(await SetAsync(engine, RuleFor(new AllowlistEntryDto("Store game", null, null, "Pkg.Game_abc"), Daily(60))));
    }

    [Fact]
    public async Task A_rule_that_is_removed_and_added_again_the_same_day_gets_its_usage_back()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(60))));
        _ = engine.AddUsage([GameKey], 1800);
        Assert.Empty(await SetAsync(engine));

        Assert.Empty(await SetAsync(engine, Rule(Daily(60))));

        Assert.Equal(1800, Assert.Single(engine.GetStatus(false).Items).UsedSeconds);
    }

    [Fact]
    public async Task The_zone_stays_frozen_while_an_app_is_blocked_and_is_captured_again_afterwards()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(null, Range(0, 9 * 60, 17 * 60))));
        Assert.Equal("UTC", engine.ZoneId);

        _zone = "Pacific/Kiritimati";
        Assert.Empty(await SetAsync(engine, Rule(null, Range(0, 9 * 60, 17 * 60), Range(1, 0, 60))));
        Assert.Equal("UTC", engine.ZoneId); // blocked at 10:00 Monday: the clock zone must not move the schedule

        SetLocal(At(0, 18));
        Assert.Empty(await SetAsync(engine, Rule(null, Range(0, 9 * 60, 17 * 60), Range(1, 0, 60))));
        Assert.Equal("Pacific/Kiritimati", engine.ZoneId);
    }

    [Fact]
    public async Task Usage_that_exhausts_the_pool_is_reported_once()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(1))));

        Assert.False(engine.AddUsage([GameKey], 30));
        Assert.True(engine.AddUsage([GameKey], 30));
        Assert.False(engine.AddUsage([GameKey], 30)); // already exhausted: the transition happened before
        Assert.False(engine.AddUsage(["path:c:\\unknown.exe"], 30));
    }

    [Fact]
    public async Task The_status_reports_usage_remaining_time_and_the_end_of_a_block_in_the_frozen_zone()
    {
        _zone = "Asia/Kolkata"; // +05:30 all year
        AppRuleEngine engine = await LoadedEngineAsync();
        var chat = new AllowlistEntryDto("Chat", ChatPath, null, null);
        var calm = new AllowlistEntryDto("Calm", @"C:\Apps\calm.exe", null, null);
        Assert.Empty(await SetAsync(engine, Rule(Daily(1)), RuleFor(chat, null, Range(0, 15 * 60, 18 * 60)), RuleFor(calm, Weekly(10))));
        _ = engine.AddUsage([GameKey], 60); // Monday 15:30 local: used up

        IReadOnlyList<AppRuleStatusDto> items = engine.GetStatus(sessionRunning: false).Items;

        AppRuleStatusDto game = items[0];
        Assert.Equal((AppRuleState.BudgetExhausted, 60L, 0L), (game.State, game.UsedSeconds, game.RemainingSeconds));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromMinutes(330)), game.BlockedUntil);
        AppRuleStatusDto chatStatus = items[1];
        Assert.Equal((AppRuleState.ScheduledBlock, 0L, (long?)null), (chatStatus.State, chatStatus.UsedSeconds, chatStatus.RemainingSeconds));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.FromMinutes(330)), chatStatus.BlockedUntil);
        AppRuleStatusDto calmStatus = items[2];
        Assert.Equal((AppRuleState.Allowed, 600L), (calmStatus.State, calmStatus.RemainingSeconds));
        Assert.Null(calmStatus.BlockedUntil);
    }

    [Fact]
    public async Task While_a_session_runs_every_rule_is_session_controlled_but_usage_still_counts()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(1), Range(0, 0, 1440))));
        _ = engine.AddUsage([GameKey], 25);

        AppRuleStatusDto status = Assert.Single(engine.GetStatus(sessionRunning: true).Items);

        Assert.Equal((AppRuleState.SessionControlled, 25L, 35L), (status.State, status.UsedSeconds, status.RemainingSeconds));
        Assert.Null(status.BlockedUntil);
    }

    [Fact]
    public async Task The_current_blocks_are_the_rules_that_block_right_now()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        var chat = new AllowlistEntryDto("Chat", ChatPath, null, null);
        var calm = new AllowlistEntryDto("Calm", @"C:\Apps\calm.exe", null, null);
        Assert.Empty(await SetAsync(engine, Rule(null, Range(0, 9 * 60, 17 * 60)), RuleFor(chat, Daily(0)), RuleFor(calm, Daily(60), Range(0, 20 * 60, 21 * 60))));

        IReadOnlyList<BlockingRule> blocks = engine.CurrentBlocks();

        Assert.Equal([(GameKey, AppRuleState.ScheduledBlock), (AppRuleIdentity.LedgerKey(chat), AppRuleState.BudgetExhausted)], blocks.Select(static b => (b.Key, b.State)));
        SetLocal(At(0, 20, 30));
        Assert.Equal([AppRuleIdentity.LedgerKey(chat), AppRuleIdentity.LedgerKey(calm)], engine.CurrentBlocks().Select(static b => b.Key));
    }

    [Fact]
    public async Task Usage_is_persisted_only_when_it_changed_and_comes_back_after_a_restart()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(60))));
        _ = engine.AddUsage([GameKey], 90);

        await engine.PersistUsageAsync(Ct);
        string usagePath = _fs.Path.Combine(Root, "usage.bin");
        DateTime written = _fs.File.GetLastWriteTimeUtc(usagePath);
        _fs.File.SetLastWriteTimeUtc(usagePath, written.AddDays(-1));
        await engine.PersistUsageAsync(Ct);

        Assert.Equal(written.AddDays(-1), _fs.File.GetLastWriteTimeUtc(usagePath)); // nothing new: not rewritten
        Assert.Equal(90, Assert.Single((await LoadedEngineAsync()).GetStatus(false).Items).UsedSeconds);
    }

    [Fact]
    public async Task Setting_rules_raises_the_changed_event()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        int raised = 0;
        engine.RulesChanged += (_, _) => raised++;

        _ = await SetAsync(engine, Rule(Daily(1)));
        _ = await SetAsync(engine, Rule(Daily(5000)));

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task The_match_of_a_foreground_image_covers_every_rule_not_only_blocked_ones()
    {
        AppRuleEngine engine = await LoadedEngineAsync();
        Assert.Empty(await SetAsync(engine, Rule(Daily(60))));

        Assert.Equal([GameKey], engine.Matcher.Match(GamePath, null, null));
    }
}
