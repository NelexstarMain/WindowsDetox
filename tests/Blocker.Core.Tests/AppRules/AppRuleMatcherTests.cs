using Blocker.Contracts;
using Blocker.Core.AppRules;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleMatcherTests
{
    private static readonly string Hash = new('c', 64);

    private static AppRuleMatcher Matcher(params AllowlistEntryDto[] apps) => new(apps.Select(a => RuleFor(a)));

    private static string KeyOf(AllowlistEntryDto app) => AppRuleIdentity.LedgerKey(app);

    [Fact]
    public void A_path_matches_however_it_is_written()
    {
        AppRuleMatcher matcher = Matcher(Game);

        Assert.Equal([KeyOf(Game)], matcher.Match(@"c:/GAMES/game.exe", null, null));
        Assert.Equal([KeyOf(Game)], matcher.Match(@"\\?\C:\Games\.\game.exe", null, null));
        Assert.Empty(matcher.Match(@"C:\Games\other.exe", null, null));
    }

    [Fact]
    public void A_hash_matches_any_path_and_any_case_and_beats_the_path_of_the_entry()
    {
        var withHash = new AllowlistEntryDto("Tool", @"C:\Tools\tool.exe", Hash, null);
        AppRuleMatcher matcher = Matcher(withHash);

        Assert.Equal([KeyOf(withHash)], matcher.Match(@"D:\renamed.exe", Hash.ToUpperInvariant(), null));
        Assert.Empty(matcher.Match(@"C:\Tools\tool.exe", new string('d', 64), null)); // a replaced binary at the old path does not match
        Assert.Empty(matcher.Match(@"C:\Tools\tool.exe", null, null)); // unreadable image: no hash, no match
    }

    [Fact]
    public void A_package_family_name_matches_case_insensitively()
    {
        var store = new AllowlistEntryDto("Store app", null, null, "Pkg.Family_abc");
        AppRuleMatcher matcher = Matcher(store);

        Assert.Equal([KeyOf(store)], matcher.Match(@"C:\Program Files\WindowsApps\x.exe", null, "pkg.family_ABC"));
        Assert.Empty(matcher.Match(@"C:\Program Files\WindowsApps\x.exe", null, "other"));
    }

    [Fact]
    public void Every_matching_rule_is_reported()
    {
        var byPath = new AllowlistEntryDto("By path", GamePath, null, null);
        var byHash = new AllowlistEntryDto("By hash", null, Hash, null);
        AppRuleMatcher matcher = Matcher(byPath, byHash);

        Assert.Equal([KeyOf(byPath), KeyOf(byHash)], matcher.Match(GamePath, Hash, null));
    }

    [Fact]
    public void The_matcher_says_which_lookups_it_needs()
    {
        Assert.False(Matcher(Game).NeedsHash);
        Assert.False(Matcher(Game).NeedsPackage);
        Assert.True(Matcher(Game, new AllowlistEntryDto("H", null, Hash, null)).NeedsHash);
        Assert.True(Matcher(new AllowlistEntryDto("P", null, null, "Pkg_x")).NeedsPackage);
        Assert.False(Matcher().NeedsHash);
        Assert.Empty(Matcher().Match(GamePath, Hash, "x"));
    }

    [Fact]
    public void The_ledger_key_prefers_the_hash_then_the_path_then_the_package()
    {
        Assert.Equal("sha256:" + Hash, KeyOf(new AllowlistEntryDto("A", GamePath, Hash.ToUpperInvariant(), "Pkg_x")));
        Assert.Equal(@"path:c:\games\game.exe", KeyOf(new AllowlistEntryDto("A", @"C:\Games\Game.exe", null, "Pkg_x")));
        Assert.Equal("pfn:pkg_x", KeyOf(new AllowlistEntryDto("A", null, null, "Pkg_X")));
    }

    [Fact]
    public void Entries_overlap_when_they_share_any_identity()
    {
        var a = new AllowlistEntryDto("A", GamePath, null, "Pkg_x");
        var b = new AllowlistEntryDto("B", @"D:\other.exe", null, "pkg_X");
        var c = new AllowlistEntryDto("C", GamePath.ToUpperInvariant(), null, null);
        var d = new AllowlistEntryDto("D", @"D:\other.exe", null, null);

        Assert.True(AppRuleIdentity.Overlap(a, b));
        Assert.True(AppRuleIdentity.Overlap(a, c));
        Assert.False(AppRuleIdentity.Overlap(b, c));
        Assert.True(AppRuleIdentity.Overlap(b, d));
    }
}
