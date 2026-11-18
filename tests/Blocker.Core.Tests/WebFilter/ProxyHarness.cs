using System.Net;
using System.Net.Sockets;
using System.Text;
using Blocker.Core.Policy;
using Blocker.Core.WebFilter;

namespace Blocker.Core.Tests.WebFilter;

/// <summary>Loopback TCP server running one handler per connection.</summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly Task _acceptLoop;
    private int _accepted;

    public LoopbackServer(Func<Socket, CancellationToken, Task> handler)
    {
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    Socket socket = await _listener.AcceptSocketAsync(_cts.Token);
                    Interlocked.Increment(ref _accepted);
                    lock (_connections)
                    {
                        _connections.Add(Task.Run(() => RunAsync(socket, handler)));
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                || (ex is InvalidOperationException && _cts.IsCancellationRequested))
            {
                // InvalidOperationException ("Not listening") when disposal stops the listener before the loop first accepts.
                return;
            }
        });
    }

    public IPEndPoint Endpoint { get; }

    public int Accepted => Volatile.Read(ref _accepted);

    private async Task RunAsync(Socket socket, Func<Socket, CancellationToken, Task> handler)
    {
        using (socket)
        {
            try
            {
                await handler(socket, _cts.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                return;
            }
        }
    }

    public static LoopbackServer Echo() => new(async (socket, ct) =>
    {
        var buffer = new byte[4096];
        int n;
        while ((n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct)) > 0)
        {
            await socket.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, ct);
        }
    });

    /// <summary>Reads one request head into <paramref name="received"/> and answers 200 "ok" with Connection: close.</summary>
    public static LoopbackServer Http(TaskCompletionSource<string> received) => new(async (socket, ct) =>
    {
        string head = await ProxyClient.ReadUntilAsync(socket, "\r\n\r\n", ct);
        received.TrySetResult(head);
        await socket.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), SocketFlags.None, ct);
    });

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        Task[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections);
        _cts.Dispose();
    }
}

/// <summary>Minimal raw-socket client for the proxy.</summary>
internal sealed class ProxyClient : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly CancellationToken _ct;

    private ProxyClient(CancellationToken ct) => _ct = ct;

    public static async Task<ProxyClient> ConnectAsync(int port, CancellationToken ct)
    {
        var client = new ProxyClient(ct);
        await client._socket.ConnectAsync(IPAddress.Loopback, port, ct);
        return client;
    }

    public async Task SendAsync(string text) => await SendAsync(Encoding.ASCII.GetBytes(text));

    public async Task SendAsync(byte[] data) => await _socket.SendAsync(data, SocketFlags.None, _ct);

    public Task<string> ReadUntilAsync(string terminator) => ReadUntilAsync(_socket, terminator, _ct);

    public async Task<byte[]> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, _ct);
            Assert.NotEqual(0, n);
            read += n;
        }

        return buffer;
    }

    /// <summary>Reads until the peer closes (a reset also counts as closed).</summary>
    public async Task<byte[]> ReadToEndAsync()
    {
        using var all = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int n;
            try
            {
                n = await _socket.ReceiveAsync(buffer, SocketFlags.None, _ct);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                break;
            }

            if (n == 0)
            {
                break;
            }

            all.Write(buffer, 0, n);
        }

        return all.ToArray();
    }

    public async Task<string> ReadTextToEndAsync() => Encoding.UTF8.GetString(await ReadToEndAsync());

    public static async Task<string> ReadUntilAsync(Socket socket, string terminator, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith(terminator, StringComparison.Ordinal))
        {
            int n = await socket.ReceiveAsync(one, SocketFlags.None, ct);
            Assert.NotEqual(0, n);
            text.Append((char)one[0]);
        }

        return text.ToString();
    }

    public void Dispose() => _socket.Dispose();
}

/// <summary>A started proxy; the resolver maps every host to the upstream, which the test owns and disposes.</summary>
internal sealed class ProxyHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _timeout;
    private readonly List<string> _blocked = [];

    private ProxyHarness(WebFilterProxy proxy, LoopbackServer? upstream, CancellationTokenSource timeout)
    {
        Proxy = proxy;
        Upstream = upstream;
        _timeout = timeout;
        proxy.RequestBlocked += (_, e) =>
        {
            lock (_blocked)
            {
                _blocked.Add(e.Host);
            }
        };
        Port = proxy.Start();
    }

    public WebFilterProxy Proxy { get; }

    public LoopbackServer? Upstream { get; }

    public int Port { get; }

    /// <summary>Snapshot of blocked hosts, taken under the same lock the proxy's event handler writes with.</summary>
    public IReadOnlyList<string> Blocked
    {
        get
        {
            lock (_blocked)
            {
                return [.. _blocked];
            }
        }
    }

    public CancellationToken Token => _timeout.Token;

    public static ProxyHarness Start(LoopbackServer? upstream, WebFilterProxyOptions? options = null)
    {
        var matcher = new DomainMatcher([DomainPattern.Parse("example.com"), DomainPattern.Parse("*.allowed.test")]);
        var proxy = new WebFilterProxy(
            new FuncWebFilterPolicy(matcher.IsAllowed),
            options,
            (_, _, _) => ValueTask.FromResult<IReadOnlyList<IPEndPoint>>(upstream is null ? [] : [upstream.Endpoint]));
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return new ProxyHarness(proxy, upstream, timeout);
    }

    public Task<ProxyClient> ConnectAsync() => ProxyClient.ConnectAsync(Port, Token);

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        _timeout.Dispose();
    }
}
