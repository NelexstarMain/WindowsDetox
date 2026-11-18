using Blocker.UI.ViewModels;

namespace Blocker.UI.Tests;

public sealed class DomainPatternValidatorTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  EXAMPLE.com  ", "example.com")]
    [InlineData("*.example.com", "*.example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    [InlineData("example.com.", "example.com")]
    public void Valid_patterns_are_normalized(string input, string expected)
    {
        DomainValidation result = DomainPatternValidator.Validate(input);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Normalized);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Wildcard_message_says_subdomains_are_included_and_exact_says_they_are_not()
    {
        Assert.Contains("every subdomain", DomainPatternValidator.Validate("*.example.com").Message);
        Assert.Contains("not its subdomains", DomainPatternValidator.Validate("example.com").Message);
    }

    [Fact]
    public void Empty_input_shows_the_examples_without_being_valid()
    {
        DomainValidation result = DomainPatternValidator.Validate("   ");

        Assert.False(result.IsValid);
        Assert.Equal(DomainPatternValidator.ExampleHint, result.Message);
    }

    [Theory]
    [InlineData("https://example.com", "without http")]
    [InlineData("example.com/path", "without a path")]
    [InlineData("example.com?x=1", "without a path")]
    [InlineData("user@example.com", "user name")]
    [InlineData("example.com:8080", "port")]
    [InlineData("exa mple.com", "spaces")]
    [InlineData("exam*ple.com", "wildcard")]
    [InlineData("*.*.example.com", "single wildcard")]
    [InlineData("a..example.com", "empty parts")]
    [InlineData("*.com", "after the wildcard")]
    [InlineData("-bad-.example.com", "not a valid domain")]
    [InlineData("192.168.0.1", "not a valid domain")]
    public void Invalid_patterns_explain_what_is_wrong(string input, string expectedFragment)
    {
        DomainValidation result = DomainPatternValidator.Validate(input);

        Assert.False(result.IsValid);
        Assert.Contains(expectedFragment, result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
