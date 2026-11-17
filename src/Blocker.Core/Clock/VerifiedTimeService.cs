using Blocker.Core.Abstractions;

namespace Blocker.Core.Clock;

/// <summary>
/// Maintains a verified-UTC anchor from independent network sources. At least two sources must agree within
/// <see cref="Agreement"/> of the median; otherwise the previous anchor is kept. Between refreshes, UTC advances on the monotonic clock only.
/// </summary>
/// <param name="sources">Independent time sources (mix of SNTP and HTTPS).</param>
/// <param name="clock">Monotonic clock.</param>
/// <param name="timeouts">Time provider for the per-source timeout (fake in tests).</param>
public sealed class VerifiedTimeService(IReadOnlyList<INetworkTimeSource> sources, IMonotonicClock clock, TimeProvider timeouts) : IVerifiedClock
{
    /// <summary>Per-source query timeout.</summary>
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Maximum distance from the median for a result to count as agreeing.</summary>
    public static readonly TimeSpan Agreement = TimeSpan.FromSeconds(2);

    private sealed record Anchor(DateTimeOffset Utc, long Tick);

    private volatile Anchor? _anchor;

    /// <summary>Gets whether a consensus anchor has been established.</summary>
    public bool HasAnchor => _anchor is not null;

    /// <inheritdoc/>
    public bool TryGetUtcNow(out DateTimeOffset utcNow)
    {
        var anchor = _anchor;
        var delta = anchor is null ? -1 : clock.TickCount64 - anchor.Tick;
        utcNow = anchor is not null && delta >= 0 ? anchor.Utc.AddMilliseconds(delta) : default;
        return anchor is not null && delta >= 0;
    }

    /// <summary>Queries all sources in parallel and updates the anchor when consensus is reached.</summary>
    /// <returns><see langword="true"/> when a new anchor was established.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(sources.Select(s => QueryAsync(s, cancellationToken))).ConfigureAwait(false);
        var now = clock.TickCount64;
        var samples = results.OfType<(DateTimeOffset Utc, long Tick)>().Select(r => r.Utc.AddMilliseconds(now - r.Tick)).Order().ToList();
        if (samples.Count < 2)
        {
            return false;
        }

        var median = Median(samples);
        var agreeing = samples.Where(s => (s - median).Duration() <= Agreement).ToList();
        if (agreeing.Count < 2)
        {
            return false;
        }

        _anchor = new Anchor(Median(agreeing), now);
        return true;
    }

    private static DateTimeOffset Median(List<DateTimeOffset> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : sorted[mid - 1] + (sorted[mid] - sorted[mid - 1]) / 2;
    }

    private async Task<(DateTimeOffset Utc, long Tick)?> QueryAsync(INetworkTimeSource source, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(SourceTimeout, timeouts);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var utc = await source.QueryUtcAsync(linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            return utc is { } value ? (value, clock.TickCount64) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // timed out
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // a faulty source is simply a missing vote
        }
    }
}
