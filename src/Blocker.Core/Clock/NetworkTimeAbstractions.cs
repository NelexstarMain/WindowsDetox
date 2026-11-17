namespace Blocker.Core.Clock;

/// <summary>A remote source of the current UTC time (SNTP server, HTTPS <c>Date</c> header, ...).</summary>
public interface INetworkTimeSource
{
    /// <summary>Gets a short display name (host) used in diagnostics.</summary>
    string Name { get; }

    /// <summary>Queries the source. Returns <see langword="null"/> when it is unreachable or its answer fails validation.</summary>
    ValueTask<DateTimeOffset?> QueryUtcAsync(CancellationToken cancellationToken = default);
}

/// <summary>A clock whose UTC value is anchored to network consensus and advanced by the monotonic clock.</summary>
public interface IVerifiedClock
{
    /// <summary>Gets the verified UTC now; <see langword="false"/> while no anchor exists.</summary>
    bool TryGetUtcNow(out DateTimeOffset utcNow);
}

/// <summary>Single request/response UDP round trip (abstracted for tests).</summary>
public interface IUdpExchange
{
    /// <summary>Sends <paramref name="request"/> to <paramref name="host"/>:<paramref name="port"/> and returns the first reply.</summary>
    /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>.</exception>
    ValueTask<byte[]> ExchangeAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default);
}
