using System.Net;
using Blocker.Core.Clock;

namespace Blocker.Core.Tests.Clock;

public sealed class HttpsDateTimeSourceTests
{
    private static readonly Uri Url = new("https://time.example/");

    private static HttpResponseMessage WithDate(string? date, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status);
        if (date is not null)
        {
            response.Headers.TryAddWithoutValidation("Date", date);
        }

        return response;
    }

    private static async Task<DateTimeOffset?> QueryAsync(Func<HttpRequestMessage, HttpResponseMessage> respond, FakeMonotonicClock? clock = null)
    {
        using var client = new HttpClient(new FakeHandler(respond));
        return await new HttpsDateTimeSource(Url, client, clock).QueryUtcAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_ParsesRfc1123DateAndAddsHalfRoundTrip()
    {
        var clock = new FakeMonotonicClock(5_000);

        var utc = await QueryAsync(
            _ =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(400));
                return WithDate("Tue, 17 Nov 2026 12:34:56 GMT");
            },
            clock);

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 12, 34, 56, 200, TimeSpan.Zero), utc);
    }

    [Fact]
    public async Task Query_UsesHeadAgainstConfiguredUrl()
    {
        var handler = new FakeHandler(_ => WithDate("Tue, 17 Nov 2026 12:34:56 GMT"));
        using var client = new HttpClient(handler);

        await new HttpsDateTimeSource(Url, client, new FakeMonotonicClock()).QueryUtcAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Head, handler.LastRequest!.Method);
        Assert.Equal(Url, handler.LastRequest.RequestUri);
    }

    [Fact]
    public async Task Query_NonSuccessStatusWithDate_StillYieldsTime()
    {
        var utc = await QueryAsync(_ => WithDate("Tue, 17 Nov 2026 12:34:56 GMT", HttpStatusCode.ServiceUnavailable), new FakeMonotonicClock());

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 12, 34, 56, TimeSpan.Zero), utc);
    }

    [Fact]
    public async Task Query_MissingDateHeader_ReturnsNull()
    {
        Assert.Null(await QueryAsync(_ => WithDate(null), new FakeMonotonicClock()));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-11-17T12:34:56Z")]
    [InlineData("Mon, 17 Nov 2026 12:34:56 GMT")] // wrong weekday
    [InlineData("")]
    public async Task Query_MalformedDate_ReturnsNull(string date)
    {
        Assert.Null(await QueryAsync(_ => WithDate(date), new FakeMonotonicClock()));
    }

    [Fact]
    public async Task Query_HttpFailure_ReturnsNull()
    {
        Assert.Null(await QueryAsync(_ => throw new HttpRequestException("boom"), new FakeMonotonicClock()));
    }

    [Theory]
    [InlineData("http://time.example/")]
    [InlineData("ftp://time.example/")]
    [InlineData("/relative")]
    public void Constructor_NonHttpsUrl_Throws(string url)
    {
        using var client = new HttpClient();

        Assert.Throws<ArgumentException>("url", () => new HttpsDateTimeSource(new Uri(url, UriKind.RelativeOrAbsolute), client));
    }

    [Fact]
    public void Name_IsHost()
    {
        using var client = new HttpClient();

        Assert.Equal("time.example", new HttpsDateTimeSource(Url, client).Name);
    }

    [Fact]
    public void ParseDate_NonGmtZone_IsRejected() => Assert.Null(HttpsDateTimeSource.ParseDate("Tue, 17 Nov 2026 12:34:56 +0100"));

    [Fact]
    public void ParseDate_Rfc1123_IsExact() =>
        Assert.Equal(new DateTimeOffset(1994, 11, 6, 8, 49, 37, TimeSpan.Zero), HttpsDateTimeSource.ParseDate(" Sun, 06 Nov 1994 08:49:37 GMT "));
}
