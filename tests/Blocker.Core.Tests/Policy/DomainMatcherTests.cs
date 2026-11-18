using Blocker.Core.Policy;

namespace Blocker.Core.Tests.Policy;

public sealed class DomainMatcherTests
{
    private static readonly string LongHost = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".example.com";

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "EXAMPLE.Com", true)]
    [InlineData("example.com", "example.com.", true)]
    [InlineData("example.com", "sub.example.com", false)]
    [InlineData("example.com", "example.org", false)]
    [InlineData("*.example.com", "sub.example.com", true)]
    [InlineData("*.example.com", "a.b.c.example.com", true)]
    [InlineData("*.example.com", "SUB.Example.COM.", true)]
    [InlineData("*.example.com", "example.com", true)]
    [InlineData("*.example.com", "notexample.com", false)]
    [InlineData("*.example.com", "example.com.evil.com", false)]
    [InlineData("*.example.com", "com", false)]
    [InlineData("bücher.de", "xn--bcher-kva.de", true)]
    [InlineData("xn--bcher-kva.de", "BÜCHER.de", true)]
    [InlineData("*.bücher.de", "shop.bücher.de", true)]
    [InlineData("*.xn--bcher-kva.de", "shop.BÜCHER.de.", true)]
    [InlineData("bücher.de", "bucher.de", false)]
    [InlineData("example.com", "93.184.216.34", false)]
    [InlineData("example.com", "127.1", false)]
    [InlineData("example.com", "0x7f.0.0.1", false)]
    [InlineData("example.com", "[::1]", false)]
    [InlineData("example.com", "", false)]
    [InlineData("example.com", ".", false)]
    [InlineData("example.com", ".example.com", false)]
    [InlineData("example.com", "a..example.com", false)]
    [InlineData("*.example.com", "a..example.com", false)]
    [InlineData("example.com", "exa mple.com", false)]
    [InlineData("example.com", "example.com:443", false)]
    public void Matches(string pattern, string host, bool expected)
    {
        var matcher = new DomainMatcher([DomainPattern.Parse(pattern)]);

        Assert.Equal(expected, matcher.IsAllowed(host));
    }

    [Fact]
    public void Overlong_hosts_are_rejected()
    {
        var matcher = new DomainMatcher([DomainPattern.Parse("*.example.com")]);
        Assert.True(LongHost.Length > 253);

        Assert.False(matcher.IsAllowed(LongHost));
    }

    [Fact]
    public void Combines_exact_and_wildcard_rules()
    {
        var matcher = new DomainMatcher(new[] { "docs.rs", "*.github.com", "github.io" }.Select(DomainPattern.Parse));

        Assert.True(matcher.IsAllowed("docs.rs"));
        Assert.True(matcher.IsAllowed("gist.github.com"));
        Assert.True(matcher.IsAllowed("github.io"));
        Assert.False(matcher.IsAllowed("me.github.io"));
        Assert.False(matcher.IsAllowed("crates.io"));
        Assert.False(new DomainMatcher([]).IsAllowed("docs.rs"));
    }

    [Theory]
    [InlineData("Example.COM.", "example.com", false)]
    [InlineData("*.Example.com", "*.example.com", true)]
    [InlineData("bücher.de", "xn--bcher-kva.de", false)]
    [InlineData("a_b.example.com", "a_b.example.com", false)]
    public void Patterns_are_normalized(string text, string expected, bool wildcard)
    {
        DomainPattern pattern = DomainPattern.Parse(text);

        Assert.Equal(expected, pattern.ToString());
        Assert.Equal(wildcard, pattern.IncludesSubdomains);
        Assert.Equal(DomainPattern.Parse(expected), pattern);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" example.com")]
    [InlineData("exa mple.com")]
    [InlineData("1.2.3.4")]
    [InlineData("127.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData("*.com")]
    [InlineData("**.example.com")]
    [InlineData("a.*.example.com")]
    [InlineData("example.*")]
    [InlineData(".example.com")]
    [InlineData("a..example.com")]
    [InlineData("-a.example.com")]
    [InlineData("a-.example.com")]
    [InlineData("exa/mple.com")]
    [InlineData("https://example.com")]
    [InlineData("example.com:443")]
    public void Invalid_patterns_are_rejected(string text)
    {
        Assert.False(DomainPattern.TryParse(text, out DomainPattern? pattern));
        Assert.Null(pattern);
        Assert.Throws<FormatException>(() => DomainPattern.Parse(text));
    }

    [Fact]
    public void Null_and_overlong_patterns_are_rejected()
    {
        Assert.False(DomainPattern.TryParse(null, out _));
        Assert.False(DomainPattern.TryParse(LongHost, out _));
        Assert.False(DomainPattern.TryParse(new string('a', 64) + ".com", out _));
    }
}
