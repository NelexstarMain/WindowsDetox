using Blocker.Core.WebFilter;

namespace Blocker.Core.Tests.WebFilter;

public sealed class PacFileTests
{
    [Fact]
    public void Routes_everything_else_through_the_proxy_with_direct_fallback()
    {
        string pac = PacFile.Generate(54321);

        Assert.Contains("function FindProxyForURL(url, host)", pac, StringComparison.Ordinal);
        Assert.Contains("isPlainHostName(host)", pac, StringComparison.Ordinal);
        Assert.Contains("host === \"localhost\"", pac, StringComparison.Ordinal);
        Assert.Contains("return \"DIRECT\";", pac, StringComparison.Ordinal);
        Assert.Contains("return \"PROXY 127.0.0.1:54321; DIRECT\";", pac, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Rejects_invalid_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PacFile.Generate(port));
    }

    [Fact]
    public void Content_type_is_the_pac_mime_type()
    {
        Assert.Equal("application/x-ns-proxy-autoconfig", PacFile.ContentType);
    }
}
