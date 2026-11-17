using System.Globalization;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Clock;

/// <summary>Reads the RFC 1123 <c>Date</c> header of an HTTPS <c>HEAD</c> response and corrects it by half the monotonic round trip.</summary>
public sealed class HttpsDateTimeSource : INetworkTimeSource
{
    private readonly Uri _url;
    private readonly HttpClient _client;
    private readonly IMonotonicClock _clock;

    /// <summary>Creates the source.</summary>
    /// <exception cref="ArgumentException"><paramref name="url"/> is not an absolute https URL.</exception>
    public HttpsDateTimeSource(Uri url, HttpClient client, IMonotonicClock? clock = null)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only https URLs are accepted.", nameof(url));
        }

        (_url, _client, _clock) = (url, client, clock ?? new SystemMonotonicClock());
    }

    /// <inheritdoc/>
    public string Name => _url.Host;

    /// <inheritdoc/>
    public async ValueTask<DateTimeOffset?> QueryUtcAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, _url);
        var sent = _clock.TickCount64;
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var rtt = TimeSpan.FromMilliseconds(_clock.TickCount64 - sent);
            return response.Headers.TryGetValues("Date", out var values) && ParseDate(values.FirstOrDefault()) is { } date ? date + rtt / 2 : null;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Parses an RFC 1123 date (<c>Sun, 06 Nov 1994 08:49:37 GMT</c>); <see langword="null"/> when malformed.</summary>
    internal static DateTimeOffset? ParseDate(string? value) =>
        value?.Trim() is { } text && text.EndsWith("GMT", StringComparison.Ordinal)
        && DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
