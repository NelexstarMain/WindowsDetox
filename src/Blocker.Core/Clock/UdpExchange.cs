using System.Net;
using System.Net.Sockets;

namespace Blocker.Core.Clock;

/// <summary>Production <see cref="IUdpExchange"/> backed by a connected <see cref="UdpClient"/> (datagrams from other peers are dropped by the OS).</summary>
public sealed class UdpExchange : IUdpExchange
{
    /// <inheritdoc/>
    public async ValueTask<byte[]> ExchangeAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            using var udp = new UdpClient(addresses[0].AddressFamily);
            udp.Connect(addresses[0], port);
            await udp.SendAsync(request, cts.Token).ConfigureAwait(false);
            var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return reply.Buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No UDP reply from {host}:{port} within {timeout}.");
        }
    }
}
