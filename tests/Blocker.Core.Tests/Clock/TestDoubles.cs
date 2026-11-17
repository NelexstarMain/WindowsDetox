using Blocker.Core.Abstractions;
using Blocker.Core.Clock;
using Blocker.Sdk.Events;

namespace Blocker.Core.Tests.Clock;

internal sealed class FakeMonotonicClock(long start = 0) : IMonotonicClock
{
    public long TickCount64 { get; set; } = start;

    public void Advance(TimeSpan by) => TickCount64 += (long)by.TotalMilliseconds;
}

internal sealed class FakeVerifiedClock : IVerifiedClock
{
    public DateTimeOffset? Utc { get; set; }

    public bool TryGetUtcNow(out DateTimeOffset utcNow)
    {
        utcNow = Utc.GetValueOrDefault();
        return Utc.HasValue;
    }
}

internal sealed class RecordingPublisher : IEventPublisher
{
    private readonly List<IBlockerEvent> _events = [];

    public IReadOnlyList<IBlockerEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask PublishAsync(IBlockerEvent evt, CancellationToken cancellationToken = default)
    {
        lock (_events)
        {
            _events.Add(evt);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeTimeSource(string name, Func<CancellationToken, ValueTask<DateTimeOffset?>> query) : INetworkTimeSource
{
    public FakeTimeSource(string name, DateTimeOffset? utc)
        : this(name, _ => ValueTask.FromResult(utc))
    {
    }

    public string Name => name;

    public ValueTask<DateTimeOffset?> QueryUtcAsync(CancellationToken cancellationToken = default) => query(cancellationToken);
}

internal sealed class FakeUdp(Func<byte[], byte[]> reply) : IUdpExchange
{
    public string? Host { get; private set; }

    public int Port { get; private set; }

    public byte[]? LastRequest { get; private set; }

    public ValueTask<byte[]> ExchangeAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        (Host, Port, LastRequest) = (host, port, request);
        return ValueTask.FromResult(reply(request));
    }
}

internal sealed class ThrowingUdp(Exception exception) : IUdpExchange
{
    public ValueTask<byte[]> ExchangeAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw exception;
}

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(respond(request));
    }
}
