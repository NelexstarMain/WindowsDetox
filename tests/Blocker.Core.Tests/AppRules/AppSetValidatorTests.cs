using Blocker.Contracts;
using Blocker.Core.AppRules;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppSetValidatorTests
{
    private static AppSetDto Set(string name, IReadOnlyList<AllowlistEntryDto>? apps = null, IReadOnlyList<string>? domains = null) =>
        new(name, apps ?? [Game], domains ?? ["example.com"]);

    private static AppSetsValidation Validate(params AppSetDto[] sets) => AppSetValidator.Validate(new AppSetsDto(sets));

    [Fact]
    public void An_empty_list_is_valid()
    {
        Assert.True(Validate().IsValid);
    }

    [Fact]
    public void Names_are_trimmed_and_the_order_is_kept()
    {
        AppSetsValidation result = Validate(Set("  Deep work  "), Set("Writing"));

        Assert.True(result.IsValid);
        Assert.Equal(["Deep work", "Writing"], result.Sets!.Sets.Select(static s => s.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    public void A_blank_name_or_one_with_control_characters_is_rejected(string name)
    {
        AppSetsValidation result = Validate(Set(name));

        Assert.False(result.IsValid);
        Assert.Null(result.Sets);
    }

    [Fact]
    public void A_name_is_at_most_forty_characters_after_trimming()
    {
        Assert.True(Validate(Set(" " + new string('x', 40) + " ")).IsValid);
        Assert.False(Validate(Set(new string('x', 41))).IsValid);
    }

    [Fact]
    public void Names_must_be_unique_ignoring_case_and_surrounding_spaces()
    {
        AppSetsValidation result = Validate(Set("Focus"), Set(" fOCUS "));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static e => e.Contains("more than one", StringComparison.Ordinal));
    }

    [Fact]
    public void At_most_fifty_sets_are_accepted()
    {
        AppSetDto[] sets = [.. Enumerable.Range(0, 51).Select(i => Set($"Set {i}"))];

        Assert.True(Validate(sets[..50]).IsValid);
        Assert.False(Validate(sets).IsValid);
    }

    [Fact]
    public void Apps_and_domains_are_checked_like_the_allowlist_checks_them()
    {
        Assert.False(Validate(Set("Bad app", [new AllowlistEntryDto("Ghost", null, null, null)])).IsValid);
        Assert.False(Validate(Set("Bad hash", [new AllowlistEntryDto("Tool", null, "abc", null)])).IsValid);
        AppSetsValidation badDomain = Validate(Set("Bad domain", domains: ["http://nope"]));
        Assert.False(badDomain.IsValid);
        Assert.Contains(badDomain.Errors, static e => e.Contains("Bad domain", StringComparison.Ordinal));
        Assert.True(Validate(Set("Wildcard", domains: ["*.docs.example.com", "example.org"])).IsValid);
    }

    [Fact]
    public void A_set_may_be_empty()
    {
        Assert.True(Validate(Set("Nothing", [], [])).IsValid);
    }

    [Fact]
    public void Errors_are_capped()
    {
        AppSetDto[] sets = [.. Enumerable.Range(0, 50).Select(static _ => Set(string.Empty))];

        AppSetsValidation result = Validate(sets);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count <= AppSetValidator.MaxErrors);
    }
}
