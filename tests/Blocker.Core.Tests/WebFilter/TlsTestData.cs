using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Blocker.Core.Tests.WebFilter;

/// <summary>ClientHello bytes: captured from a real <see cref="SslStream"/> or assembled by hand for edge cases.</summary>
internal static class TlsTestData
{
    /// <summary>Starts a TLS client handshake against a loopback socket and returns the first record it sends.</summary>
    public static async Task<byte[]> CaptureRealClientHelloAsync(string serverName, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
        using Socket server = await listener.AcceptSocketAsync(cancellationToken);
        await using var ssl = new SslStream(client.GetStream());
        Task handshake = ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, cancellationToken);

        var buffer = new byte[16 * 1024 + 5];
        int read = 0;
        int needed = 5;
        while (read < needed)
        {
            int n = await server.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cancellationToken);
            Assert.NotEqual(0, n);
            read += n;
            if (read >= 5)
            {
                needed = 5 + ((buffer[3] << 8) | buffer[4]);
            }
        }

        server.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => handshake);
        return buffer[..needed];
    }

    public static byte[] Extension(int type, byte[] data) => [.. U16(type), .. U16(data.Length), .. data];

    public static byte[] ServerNameExtension(params string[] hostNames) => ServerNameExtension(hostNames.Select(n => (0, System.Text.Encoding.UTF8.GetBytes(n))).ToArray());

    public static byte[] ServerNameExtension(params (int NameType, byte[] Value)[] names)
    {
        byte[] list = [.. names.SelectMany(n => (byte[])[(byte)n.NameType, .. U16(n.Value.Length), .. n.Value])];
        return Extension(0, [.. U16(list.Length), .. list]);
    }

    /// <summary>A single-record ClientHello with the given extensions (null: no extensions block at all).</summary>
    public static byte[] ClientHello(IReadOnlyList<byte[]>? extensions, int sessionIdLength = 0)
    {
        byte[] body =
        [
            3, 3,
            .. new byte[32],
            (byte)sessionIdLength, .. new byte[sessionIdLength],
            .. U16(4), 0x13, 0x01, 0xc0, 0x2f,
            1, 0,
        ];
        if (extensions is not null)
        {
            byte[] all = [.. extensions.SelectMany(e => e)];
            body = [.. body, .. U16(all.Length), .. all];
        }

        byte[] handshake = [1, (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];
        return Record(handshake);
    }

    public static byte[] Record(byte[] fragment, byte contentType = 0x16) => [contentType, 3, 1, .. U16(fragment.Length), .. fragment];

    public static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];
}
