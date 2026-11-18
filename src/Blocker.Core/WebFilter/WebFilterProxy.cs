using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Blocker.Core.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocker.Core.WebFilter;

/// <summary>
/// Local filtering proxy bound to 127.0.0.1. Supports HTTP <c>CONNECT</c> (ports 80/443; the tunnel must open with a TLS ClientHello whose SNI equals the
/// CONNECT host) and plain absolute-URI HTTP requests (one request per connection, <c>Connection: close</c>). Also serves the PAC file at <c>GET /proxy.pac</c>.
/// IP literals are always refused. A disallowed CONNECT host gets <c>403</c> before the tunnel opens; SNI problems close the already opened tunnel.
/// </summary>
public sealed class WebFilterProxy : IAsyncDisposable
{
    private static readonly byte[] ConnectEstablished = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

    private readonly IWebFilterPolicy _policy;
    private readonly WebFilterProxyOptions _options;
    private readonly UpstreamResolver _resolver;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private int _active;
    private volatile bool _stopping;

    /// <summary>Creates the proxy.</summary>
    /// <param name="policy">Domain decision (for example <c>new FuncWebFilterPolicy(matcher.IsAllowed)</c>).</param>
    /// <param name="options">Limits; defaults when null.</param>
    /// <param name="resolver">Upstream resolver; system DNS (loopback answers dropped) when null.</param>
    /// <param name="logger">Optional logger.</param>
    public WebFilterProxy(IWebFilterPolicy policy, WebFilterProxyOptions? options = null, UpstreamResolver? resolver = null, ILogger<WebFilterProxy>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        (_policy, _options, _resolver) = (policy, options ?? new WebFilterProxyOptions(), resolver ?? ResolveViaDnsAsync);
        _logger = logger ?? NullLogger<WebFilterProxy>.Instance;
    }

    /// <summary>Raised when a request is refused. Handler exceptions are logged and swallowed.</summary>
    public event EventHandler<RequestBlockedEventArgs>? RequestBlocked;

    /// <summary>Listening port; 0 before <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Starts listening and returns the port.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public int Start()
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("The proxy is already started.");
        }

        var listener = new TcpListener(IPAddress.Loopback, _options.Port) { ExclusiveAddressUse = true };
        listener.Start(256);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(listener, _cts.Token);
        return Port;
    }

    /// <summary>Stops accepting, drops all connections and waits for them to finish.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        _stopping = true;
        await cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        await _acceptLoop.ConfigureAwait(false);
        if (Volatile.Read(ref _active) == 0)
        {
            _drained.TrySetResult();
        }

        await _drained.Task.ConfigureAwait(false);
        cts.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept failed.");
                continue;
            }

            if (Interlocked.Increment(ref _active) > _options.MaxConnections)
            {
                client.Dispose();
                Release();
                continue;
            }

            _ = HandleAsync(client, stop);
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _active) == 0 && _stopping)
        {
            _drained.TrySetResult();
        }
    }

    private async Task HandleAsync(Socket client, CancellationToken stop)
    {
        try
        {
            client.NoDelay = true;
            await ServeAsync(client, stop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection ended with an error.");
        }
        finally
        {
            client.Dispose();
            Release();
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken stop)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(_options.MaxHeaderBytes, TlsClientHelloParser.MaxRecordSize));
        try
        {
            int count = 0;
            int headEnd = -1;
            while (headEnd < 0)
            {
                if (count >= _options.MaxHeaderBytes)
                {
                    await RespondAsync(client, "431 Request Header Fields Too Large", "text/plain", "Request header too large.", idle.Token).ConfigureAwait(false);
                    return;
                }

                idle.CancelAfter(_options.IdleTimeout);
                int n = await client.ReceiveAsync(buffer.AsMemory(count, _options.MaxHeaderBytes - count), SocketFlags.None, idle.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }

                int from = Math.Max(0, count - 3);
                count += n;
                int index = buffer.AsSpan(from, count - from).IndexOf("\r\n\r\n"u8);
                headEnd = index < 0 ? -1 : from + index + 4;
            }

            RequestHead? head = RequestHead.TryParse(buffer.AsSpan(0, headEnd));
            if (head is null)
            {
                await RespondAsync(client, "400 Bad Request", "text/plain", "Malformed request.", idle.Token).ConfigureAwait(false);
                return;
            }

            // Bytes received after the head (pipelined body / early ClientHello) move to the buffer start.
            int leftover = count - headEnd;
            buffer.AsSpan(headEnd, leftover).CopyTo(buffer);
            if (head.Method == "CONNECT")
            {
                await ServeConnectAsync(client, head, buffer, leftover, idle, stop).ConfigureAwait(false);
            }
            else if (head.Target.StartsWith('/'))
            {
                await ServeLocalAsync(client, head, idle.Token).ConfigureAwait(false);
            }
            else
            {
                await ServePlainHttpAsync(client, head, buffer, leftover, idle, stop).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ServeLocalAsync(Socket client, RequestHead head, CancellationToken ct)
    {
        string path = head.Target;
        int query = path.IndexOf('?', StringComparison.Ordinal);
        if (head.Method == "GET" && (query < 0 ? path : path[..query]) == "/proxy.pac")
        {
            await RespondAsync(client, "200 OK", PacFile.ContentType, PacFile.Generate(Port), ct).ConfigureAwait(false);
        }
        else
        {
            await RespondAsync(client, "404 Not Found", "text/plain", "Not found.", ct).ConfigureAwait(false);
        }
    }

    private async Task ServeConnectAsync(Socket client, RequestHead head, byte[] buffer, int count, CancellationTokenSource idle, CancellationToken stop)
    {
        if (!DomainName.TrySplitAuthority(head.Target, 443, out string? rawHost, out int port)
            || port is not (80 or 443)
            || !DomainName.TryNormalize(rawHost, out string? host)
            || !_policy.IsAllowed(host))
        {
            await BlockWithPageAsync(client, rawHost ?? head.Target, idle.Token).ConfigureAwait(false);
            return;
        }

        await client.SendAsync(ConnectEstablished, SocketFlags.None, idle.Token).ConfigureAwait(false);

        ClientHelloStatus status;
        string? sni;
        while ((status = TlsClientHelloParser.Parse(buffer.AsSpan(0, count), out sni)) == ClientHelloStatus.NeedMoreData)
        {
            if (count >= TlsClientHelloParser.MaxRecordSize)
            {
                status = ClientHelloStatus.Invalid;
                break;
            }

            idle.CancelAfter(_options.IdleTimeout);
            int n = await client.ReceiveAsync(buffer.AsMemory(count, TlsClientHelloParser.MaxRecordSize - count), SocketFlags.None, idle.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            count += n;
        }

        if (status != ClientHelloStatus.Found
            || !DomainName.TryNormalize(sni, out string? sniHost)
            || !string.Equals(sniHost, host, StringComparison.Ordinal)
            || !_policy.IsAllowed(sniHost))
        {
            OnBlocked(rawHost);
            return;
        }

        using Socket? upstream = await ConnectUpstreamAsync(host, port, stop).ConfigureAwait(false);
        if (upstream is null)
        {
            return;
        }

        await upstream.SendAsync(buffer.AsMemory(0, count), SocketFlags.None, idle.Token).ConfigureAwait(false);
        await RelayAsync(client, upstream, idle).ConfigureAwait(false);
    }

    private async Task ServePlainHttpAsync(Socket client, RequestHead head, byte[] buffer, int count, CancellationTokenSource idle, CancellationToken stop)
    {
        const string Scheme = "http://";
        if (!head.Target.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(client, "400 Bad Request", "text/plain", "Only absolute http:// URIs and CONNECT are supported.", idle.Token).ConfigureAwait(false);
            return;
        }

        string rest = head.Target[Scheme.Length..];
        int pathStart = rest.AsSpan().IndexOfAny('/', '?');
        string authority = pathStart < 0 ? rest : rest[..pathStart];
        string path = pathStart < 0 ? "/" : rest[pathStart] == '?' ? "/" + rest[pathStart..] : rest[pathStart..];
        if (!DomainName.TrySplitAuthority(authority, 80, out string? rawHost, out int port))
        {
            await RespondAsync(client, "400 Bad Request", "text/plain", "Malformed request target.", idle.Token).ConfigureAwait(false);
            return;
        }

        if (port is not (80 or 443)
            || !DomainName.TryNormalize(rawHost, out string? host)
            || !_policy.IsAllowed(host)
            || !HostHeaderMatches(head.Host, host, port))
        {
            await BlockWithPageAsync(client, rawHost, idle.Token).ConfigureAwait(false);
            return;
        }

        using Socket? upstream = await ConnectUpstreamAsync(host, port, stop).ConfigureAwait(false);
        if (upstream is null)
        {
            await RespondAsync(client, "502 Bad Gateway", "text/plain", "Upstream unreachable.", idle.Token).ConfigureAwait(false);
            return;
        }

        var request = new ArrayBufferWriter<byte>(512 + head.ForwardHeaders.Length + count);
        request.Write(Encoding.ASCII.GetBytes($"{head.Method} {path} HTTP/1.1\r\nHost: {host}{(port == 80 ? "" : ":" + port)}\r\n"));
        request.Write(head.ForwardHeaders);
        request.Write("Connection: close\r\n\r\n"u8);
        request.Write(buffer.AsSpan(0, count));
        await upstream.SendAsync(request.WrittenMemory, SocketFlags.None, idle.Token).ConfigureAwait(false);
        await RelayAsync(client, upstream, idle).ConfigureAwait(false);
    }

    private static bool HostHeaderMatches(string? hostHeader, string host, int port) =>
        hostHeader is null
        || (DomainName.TrySplitAuthority(hostHeader, 80, out string? headerHost, out int headerPort)
            && headerPort == port
            && DomainName.TryNormalize(headerHost, out string? normalized)
            && string.Equals(normalized, host, StringComparison.Ordinal));

    private async Task BlockWithPageAsync(Socket client, string host, CancellationToken ct)
    {
        OnBlocked(host);
        string safe = WebUtility.HtmlEncode(host.Length > 255 ? host[..255] : host);
        await RespondAsync(client, "403 Forbidden", "text/html; charset=utf-8", $"<!doctype html><title>Blocked</title><h1>Blocked</h1><p>{safe} is not on the allowlist.</p>", ct).ConfigureAwait(false);
    }

    private static async Task RespondAsync(Socket client, string status, string contentType, string body, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await client.SendAsync(head, SocketFlags.None, ct).ConfigureAwait(false);
        await client.SendAsync(payload, SocketFlags.None, ct).ConfigureAwait(false);
    }

    private void OnBlocked(string host)
    {
        EventHandler<RequestBlockedEventArgs>? handlers = RequestBlocked;
        if (handlers is null)
        {
            return;
        }

        var args = new RequestBlockedEventArgs(host.Length > 255 ? host[..255] : host);
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<RequestBlockedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RequestBlocked handler failed.");
            }
        }
    }

    private async Task<Socket?> ConnectUpstreamAsync(string host, int port, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            IReadOnlyList<IPEndPoint> endpoints = await _resolver(host, port, timeout.Token).ConfigureAwait(false);
            foreach (IPEndPoint endpoint in endpoints)
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    return socket;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "Connect to {Endpoint} failed.", endpoint);
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Resolving {Host} failed.", host);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _logger.LogDebug("Connect to {Host} timed out.", host);
        }

        return null;
    }

    private async Task RelayAsync(Socket client, Socket upstream, CancellationTokenSource idle)
    {
        Task up = PumpAsync(client, upstream, idle);
        Task down = PumpAsync(upstream, client, idle);
        await Task.WhenAll(up, down).ConfigureAwait(false);
    }

    private async Task PumpAsync(Socket from, Socket to, CancellationTokenSource idle)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                idle.CancelAfter(_options.IdleTimeout);
                int n = await from.ReceiveAsync(buffer, SocketFlags.None, idle.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                await to.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, idle.Token).ConfigureAwait(false);
            }

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Relay ended.");
            await idle.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<IReadOnlyList<IPEndPoint>> ResolveViaDnsAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var endpoints = new List<IPEndPoint>(addresses.Length);
        foreach (IPAddress address in addresses)
        {
            if (!IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            {
                endpoints.Add(new IPEndPoint(address, port));
            }
        }

        return endpoints;
    }
}
