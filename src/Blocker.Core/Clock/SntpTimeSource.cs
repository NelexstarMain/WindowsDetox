using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Clock;

/// <summary>
/// SNTP v4 client. The transmit timestamp of the request is a random nonce (no wall clock is ever read); the reply must echo it.
/// UTC is derived as <c>T3 + (RTT - (T3 - T2)) / 2</c>, the NTP four-timestamp estimate with the round trip measured on the monotonic clock.
/// </summary>
/// <param name="host">NTP server host name.</param>
/// <param name="udp">UDP transport.</param>
/// <param name="clock">Monotonic clock used to measure the round trip (defaults to the system clock).</param>
public sealed class SntpTimeSource(string host, IUdpExchange udp, IMonotonicClock? clock = null) : INetworkTimeSource
{
    private const int Port = 123;
    private const int PacketLength = 48;
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private static readonly DateTimeOffset NtpEra0 = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(4);

    private readonly IMonotonicClock _clock = clock ?? new SystemMonotonicClock();

    /// <inheritdoc/>
    public string Name => host;

    /// <inheritdoc/>
    public async ValueTask<DateTimeOffset?> QueryUtcAsync(CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(out var nonce);
        byte[] reply;
        var sent = _clock.TickCount64;
        try
        {
            reply = await udp.ExchangeAsync(host, Port, request, ExchangeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
        {
            return null;
        }

        return ParseReply(reply, nonce, TimeSpan.FromMilliseconds(_clock.TickCount64 - sent));
    }

    /// <summary>Builds the 48-byte client request; <paramref name="nonce"/> is the random transmit timestamp.</summary>
    internal static byte[] BuildRequest(out ulong nonce)
    {
        var packet = new byte[PacketLength];
        packet[0] = 0b00_100_011; // LI 0, VN 4, mode 3 (client)
        do
        {
            nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        }
        while (nonce == 0);

        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(40), nonce);
        return packet;
    }

    /// <summary>Validates <paramref name="reply"/> and returns the estimated UTC at reception, or <see langword="null"/> when invalid.</summary>
    internal static DateTimeOffset? ParseReply(ReadOnlySpan<byte> reply, ulong nonce, TimeSpan roundTrip)
    {
        if (reply.Length < PacketLength)
        {
            return null;
        }

        int leap = reply[0] >> 6, mode = reply[0] & 0b111, stratum = reply[1];
        if (mode != 4 || stratum is < 1 or > 15 || leap == 3)
        {
            return null;
        }

        var originate = BinaryPrimitives.ReadUInt64BigEndian(reply[24..]);
        var receive = BinaryPrimitives.ReadUInt64BigEndian(reply[32..]);
        var transmit = BinaryPrimitives.ReadUInt64BigEndian(reply[40..]);
        if (originate != nonce || transmit == 0 || receive == 0)
        {
            return null;
        }

        var t2 = FromNtp(receive);
        var t3 = FromNtp(transmit);
        var delay = roundTrip - (t3 - t2);
        return t3 + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay) / 2;
    }

    /// <summary>Converts a 32.32 fixed-point NTP timestamp (era 0 when the high bit is set, era 1 otherwise) to UTC.</summary>
    internal static DateTimeOffset FromNtp(ulong timestamp)
    {
        var seconds = timestamp >> 32;
        var fraction = timestamp & 0xFFFFFFFF;
        if ((seconds & 0x8000_0000) == 0)
        {
            seconds += 1UL << 32;
        }

        return NtpEra0.AddTicks((long)seconds * TicksPerSecond + (long)((fraction * (ulong)TicksPerSecond) >> 32));
    }

    /// <summary>Converts UTC to a 32.32 NTP timestamp (inverse of <see cref="FromNtp"/> at 100 ns resolution; used by tests and fakes).</summary>
    internal static ulong ToNtp(DateTimeOffset utc)
    {
        var ticks = (utc - NtpEra0).Ticks;
        var seconds = (ulong)(ticks / TicksPerSecond);
        var fraction = (((ulong)(ticks % TicksPerSecond) << 32) + TicksPerSecond - 1) / (ulong)TicksPerSecond; // rounded up so FromNtp round-trips exactly
        return (seconds << 32) | fraction;
    }
}
