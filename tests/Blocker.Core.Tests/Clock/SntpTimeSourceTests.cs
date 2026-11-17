using System.Buffers.Binary;
using System.Net.Sockets;
using Blocker.Core.Clock;

namespace Blocker.Core.Tests.Clock;

public sealed class SntpTimeSourceTests
{
    private static readonly DateTimeOffset T2 = new(2026, 11, 17, 12, 0, 0, 100, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 11, 17, 12, 0, 0, 150, TimeSpan.Zero);

    private static byte[] Reply(byte[] request, byte firstByte = 0b00_100_100, byte stratum = 2, ulong? originate = null, ulong? transmit = null, int length = 48)
    {
        var reply = new byte[Math.Max(length, 48)];
        reply[0] = firstByte;
        reply[1] = stratum;
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), originate ?? BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(40)));
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(32), SntpTimeSource.ToNtp(T2));
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(40), transmit ?? SntpTimeSource.ToNtp(T3));
        return length < 48 ? reply[..length] : reply;
    }

    private static async Task<DateTimeOffset?> QueryAsync(Func<byte[], byte[]> reply, long roundTripMs = 300)
    {
        var clock = new FakeMonotonicClock(1_000);
        var udp = new FakeUdp(request =>
        {
            clock.TickCount64 += roundTripMs;
            return reply(request);
        });
        return await new SntpTimeSource("time.example", udp, clock).QueryUtcAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Request_IsSntpV4ClientPacketWithNonZeroNonce()
    {
        var request = SntpTimeSource.BuildRequest(out var nonce);

        Assert.Equal(48, request.Length);
        Assert.Equal(0x23, request[0]);
        Assert.NotEqual(0UL, nonce);
        Assert.Equal(nonce, BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(40)));
        Assert.All(request.AsSpan(1, 39).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Request_NonceIsRandomPerCall()
    {
        SntpTimeSource.BuildRequest(out var a);
        SntpTimeSource.BuildRequest(out var b);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task ValidReply_ReturnsTransmitPlusHalfOfNetworkDelay()
    {
        // RTT 300 ms, server processing T3-T2 = 50 ms -> network delay 250 ms -> T3 + 125 ms.
        var utc = await QueryAsync(request => Reply(request));

        Assert.Equal(T3.AddMilliseconds(125), utc);
    }

    [Fact]
    public async Task ValidReply_ZeroLatency_ReturnsTransmitTimestamp()
    {
        // Processing time equals the whole round trip: delay 0.
        var utc = await QueryAsync(request => Reply(request), roundTripMs: 50);

        Assert.Equal(T3, utc);
    }

    [Fact]
    public async Task ValidReply_RoundTripShorterThanProcessing_ClampsDelayToZero()
    {
        var utc = await QueryAsync(request => Reply(request), roundTripMs: 10);

        Assert.Equal(T3, utc);
    }

    [Fact]
    public async Task Query_SendsToPort123OfConfiguredHost()
    {
        var udp = new FakeUdp(request => Reply(request));
        await new SntpTimeSource("pool.example", udp, new FakeMonotonicClock()).QueryUtcAsync(TestContext.Current.CancellationToken);

        Assert.Equal("pool.example", udp.Host);
        Assert.Equal(123, udp.Port);
        Assert.Equal(48, udp.LastRequest!.Length);
    }

    [Theory]
    [InlineData(0b00_100_100)] // leap 0, v4, server
    [InlineData(0b01_100_100)] // leap 1 (+1 s) is fine
    [InlineData(0b10_100_100)] // leap 2 (-1 s) is fine
    [InlineData(0b00_011_100)] // v3 server
    public async Task Reply_AcceptedHeaders_AreAccepted(int firstByte)
    {
        var utc = await QueryAsync(request => Reply(request, (byte)firstByte));

        Assert.NotNull(utc);
    }

    [Theory]
    [InlineData(0b00_100_011)] // mode 3 (client) - reflected request
    [InlineData(0b00_100_000)] // mode 0
    [InlineData(0b00_100_101)] // mode 5 (broadcast)
    [InlineData(0b00_100_110)] // mode 6 (control)
    public async Task Reply_WrongMode_IsRejected(int firstByte)
    {
        Assert.Null(await QueryAsync(request => Reply(request, (byte)firstByte)));
    }

    [Theory]
    [InlineData(0)] // kiss-o'-death
    [InlineData(16)]
    [InlineData(255)]
    public async Task Reply_BadStratum_IsRejected(int stratum)
    {
        Assert.Null(await QueryAsync(request => Reply(request, stratum: (byte)stratum)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public async Task Reply_BoundaryStratum_IsAccepted(int stratum)
    {
        Assert.NotNull(await QueryAsync(request => Reply(request, stratum: (byte)stratum)));
    }

    [Fact]
    public async Task Reply_LeapAlarm_IsRejected()
    {
        Assert.Null(await QueryAsync(request => Reply(request, firstByte: 0b11_100_100)));
    }

    [Fact]
    public async Task Reply_WrongOriginateNonce_IsRejected()
    {
        Assert.Null(await QueryAsync(request => Reply(request, originate: BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(40)) + 1)));
    }

    [Fact]
    public async Task Reply_ZeroOriginate_IsRejected()
    {
        Assert.Null(await QueryAsync(request => Reply(request, originate: 0)));
    }

    [Fact]
    public async Task Reply_ZeroTransmitTimestamp_IsRejected()
    {
        Assert.Null(await QueryAsync(request => Reply(request, transmit: 0)));
    }

    [Fact]
    public async Task Reply_TruncatedPacket_IsRejected()
    {
        Assert.Null(await QueryAsync(request => Reply(request, length: 47)));
        Assert.Null(await QueryAsync(_ => []));
    }

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task NetworkFailure_ReturnsNull(Exception failure)
    {
        var source = new SntpTimeSource("time.example", new ThrowingUdp(failure), new FakeMonotonicClock());

        Assert.Null(await source.QueryUtcAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<Exception> NetworkFailures() =>
        new() { new TimeoutException(), new SocketException((int)SocketError.HostNotFound), new IOException("closed") };

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var source = new SntpTimeSource("time.example", new ThrowingUdp(new OperationCanceledException()), new FakeMonotonicClock());

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await source.QueryUtcAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Name_IsHost() => Assert.Equal("time.example", new SntpTimeSource("time.example", new FakeUdp(r => r)).Name);

    [Theory]
    [InlineData(2026, 11, 17, 12, 0, 0)] // era 0 (high bit of seconds set)
    [InlineData(2036, 2, 7, 6, 28, 15)] // last second of era 0
    [InlineData(2036, 2, 7, 6, 28, 16)] // era 1 starts
    [InlineData(2040, 1, 1, 0, 0, 0)] // era 1
    public void NtpTimestamp_RoundTripsAcrossEraRollover(int y, int mo, int d, int h, int mi, int s)
    {
        var utc = new DateTimeOffset(y, mo, d, h, mi, s, 123, TimeSpan.Zero).AddTicks(4567);

        Assert.Equal(utc, SntpTimeSource.FromNtp(SntpTimeSource.ToNtp(utc)));
    }

    [Fact]
    public void FromNtp_KnownTimestamp_IsExact()
    {
        // 3_906_000_000 s after 1900-01-01, converted independently via the NTP-to-Unix offset 2_208_988_800.
        var unixSeconds = 3_906_000_000L - 2_208_988_800L;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).AddMilliseconds(500), SntpTimeSource.FromNtp((3_906_000_000UL << 32) | 0x8000_0000UL));
    }
}
