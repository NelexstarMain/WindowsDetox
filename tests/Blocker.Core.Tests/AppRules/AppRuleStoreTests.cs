using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.AppRules;
using Blocker.Core.Security;
using Blocker.Core.Tests.Security;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleStoreTests
{
    private static readonly string Root = MockUnixSupport.Path(@"C:\ProgramData\WindowsDetox");

    private readonly MockFileSystem _fs = new();
    private readonly SecureFileStore _store;

    public AppRuleStoreTests()
    {
        var protector = new FakeSecretProtector();
        _store = new SecureFileStore(_fs, protector, new HmacKeyStore(_fs, protector, _fs.Path.Combine(Root, "hmac.key")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PathOf(string name) => _fs.Path.Combine(Root, name);

    [Fact]
    public async Task App_sets_round_trip_and_a_damaged_file_means_no_sets()
    {
        var store = new AppSetsStore(_store, PathOf("sets.bin"));
        Assert.Empty((await store.LoadAsync(Ct)).Sets);
        var saved = new AppSetsDto([new AppSetDto("Deep work", [Game], ["example.com"])]);

        await store.SaveAsync(saved, Ct);
        AppSetsDto loaded = await store.LoadAsync(Ct);

        AppSetDto set = Assert.Single(loaded.Sets);
        Assert.Equal("Deep work", set.Name);
        Assert.Equal(Game, Assert.Single(set.Apps));
        Assert.Equal(["example.com"], set.Domains);
        _fs.File.WriteAllBytes(PathOf("sets.bin"), new byte[64]);
        Assert.Empty((await store.LoadAsync(Ct)).Sets);
    }

    [Fact]
    public async Task App_rules_round_trip_with_their_frozen_time_zone()
    {
        var store = new AppRulesStore(_store, PathOf("rules.bin"));
        StoredAppRules none = await store.LoadAsync(Ct);
        Assert.Empty(none.Rules.Rules);
        Assert.Null(none.TimeZoneId);
        var rules = new AppRulesDto([Rule(PerWeekday(0, 10, 20, 30, 40, 50, 60), Range(1, 60, 120), Range(3, 0, 1440))]);

        await store.SaveAsync(new StoredAppRules(rules, "Europe/Warsaw"), Ct);
        StoredAppRules loaded = await store.LoadAsync(Ct);

        Assert.Equal("Europe/Warsaw", loaded.TimeZoneId);
        AppRuleDto rule = Assert.Single(loaded.Rules.Rules);
        Assert.Equal(Game, rule.App);
        Assert.Equal([new WeeklyRangeDto(1, 60, 120), new WeeklyRangeDto(3, 0, 1440)], rule.BlockedRanges);
        Assert.Equal(AppBudgetMode.PerWeekday, rule.Budget.Mode);
        Assert.Equal([0, 10, 20, 30, 40, 50, 60], rule.Budget.PerWeekdayMinutes);
    }

    [Fact]
    public async Task A_damaged_or_incomplete_rules_file_means_no_rules()
    {
        var store = new AppRulesStore(_store, PathOf("rules.bin"));
        _fs.Directory.CreateDirectory(Root);
        _fs.File.WriteAllBytes(PathOf("rules.bin"), new byte[64]);
        Assert.Empty((await store.LoadAsync(Ct)).Rules.Rules);

        await _store.WriteAsync(PathOf("rules.bin"), "{\"unrelated\":1}"u8.ToArray(), Ct);

        Assert.Empty((await store.LoadAsync(Ct)).Rules.Rules);
    }

    [Fact]
    public async Task App_usage_round_trips_and_a_damaged_file_means_no_usage()
    {
        var store = new AppUsageStore(_store, PathOf("usage.bin"));
        Assert.Empty((await store.LoadAsync(Ct)).Entries);
        var ledger = new AppUsageLedger();
        ledger.Add("path:c:\\games\\game.exe", 125, At(1, 10));

        await store.SaveAsync(ledger.State, Ct);
        var restored = new AppUsageLedger(await store.LoadAsync(Ct));

        Assert.Equal(125, restored.DaySeconds("path:c:\\games\\game.exe", At(1, 11)));
        Assert.Equal(125, restored.WeekSeconds("path:c:\\games\\game.exe", At(1, 11)));
        _fs.File.WriteAllBytes(PathOf("usage.bin"), new byte[64]);
        Assert.Empty((await store.LoadAsync(Ct)).Entries);
    }
}
