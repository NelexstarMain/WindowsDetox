using System.Net;
using System.Net.Sockets;
using Blocker.Core.Clock;

namespace Blocker.Core.Tests.Clock;

public sealed class UdpExchangeTests
{
    private static (UdpClient Server, int Port) StartServer()
    {
        var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return (server, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task Exchange_ReturnsReplyFromServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, port) = StartServer();
        using var _ = server;
        var serverTask = Task.Run(
            async () =>
            {
                var received = await server.ReceiveAsync(ct);
                await server.SendAsync(received.Buffer.Reverse().ToArray(), received.RemoteEndPoint, ct);
            },
            ct);

        var reply = await new UdpExchange().ExchangeAsync("127.0.0.1", port, [1, 2, 3], TimeSpan.FromSeconds(10), ct);
        await serverTask;

        Assert.Equal(new byte[] { 3, 2, 1 }, reply);
    }

    [Fact]
    public async Task Exchange_NoReply_ThrowsTimeoutException()
    {
        var (server, port) = StartServer();
        using var _ = server;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await new UdpExchange().ExchangeAsync("127.0.0.1", port, [1], TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exchange_CancelledByCaller_ThrowsOperationCanceled()
    {
        var (server, port) = StartServer();
        using var _ = server;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new UdpExchange().ExchangeAsync("127.0.0.1", port, [1], TimeSpan.FromSeconds(10), cts.Token));
    }
}
