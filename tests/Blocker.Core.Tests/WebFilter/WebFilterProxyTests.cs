using System.Text;
using Blocker.Core.WebFilter;
using static Blocker.Core.Tests.WebFilter.TlsTestData;

namespace Blocker.Core.Tests.WebFilter;

public sealed class WebFilterProxyTests
{
    private static string Connect(string authority) => $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n";

    [Fact]
    public async Task Allowed_connect_tunnels_the_client_hello_and_later_bytes_to_the_upstream()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        byte[] hello = await CaptureRealClientHelloAsync("example.com", harness.Token);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect("example.com:443"));
        Assert.StartsWith("HTTP/1.1 200", await client.ReadUntilAsync("\r\n\r\n"), StringComparison.Ordinal);
        await client.SendAsync(hello);
        Assert.Equal(hello, await client.ReadExactAsync(hello.Length));
        await client.SendAsync("ping");
        Assert.Equal("ping", Encoding.ASCII.GetString(await client.ReadExactAsync(4)));

        Assert.Empty(harness.Blocked);
        Assert.Equal(1, upstream.Accepted);
    }

    [Fact]
    public async Task Connect_host_matches_wildcard_and_ignores_case()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();
        byte[] hello = ClientHello([ServerNameExtension("sub.allowed.test")]);

        await client.SendAsync(Connect("SUB.Allowed.TEST:443"));
        await client.ReadUntilAsync("\r\n\r\n");
        await client.SendAsync(hello);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length));
    }

    [Fact]
    public async Task Client_hello_sent_together_with_the_connect_request_is_not_lost()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();
        byte[] hello = ClientHello([ServerNameExtension("example.com")]);

        await client.SendAsync([.. Encoding.ASCII.GetBytes(Connect("example.com:443")), .. hello]);
        await client.ReadUntilAsync("\r\n\r\n");

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length));
    }

    [Fact]
    public async Task Fragmented_client_hello_is_reassembled()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();
        byte[] hello = ClientHello([ServerNameExtension("example.com")]);

        await client.SendAsync(Connect("example.com:443"));
        await client.ReadUntilAsync("\r\n\r\n");
        await client.SendAsync(hello[..3]);
        await client.SendAsync(hello[3..20]);
        await client.SendAsync(hello[20..]);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length));
    }

    [Fact]
    public async Task Disallowed_connect_host_gets_403_before_the_tunnel_and_raises_the_event()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect("evil.test:443"));
        string response = await client.ReadTextToEndAsync();

        Assert.StartsWith("HTTP/1.1 403", response, StringComparison.Ordinal);
        Assert.Equal(["evil.test"], harness.Blocked);
        Assert.Equal(0, upstream.Accepted);
    }

    [Theory]
    [InlineData("127.0.0.1:443")]
    [InlineData("93.184.216.34:443")]
    [InlineData("[::1]:443")]
    [InlineData("2130706433:443")]
    [InlineData("example.com:8080")]
    [InlineData("example.com:22")]
    [InlineData("example.com.evil.test:443")]
    public async Task Ip_literals_and_unsupported_ports_get_403(string authority)
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect(authority));

        Assert.StartsWith("HTTP/1.1 403", await client.ReadTextToEndAsync(), StringComparison.Ordinal);
        Assert.Single(harness.Blocked);
        Assert.Equal(0, upstream.Accepted);
    }

    [Fact]
    public async Task Sni_that_differs_from_the_connect_host_closes_the_tunnel()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect("example.com:443"));
        await client.ReadUntilAsync("\r\n\r\n");
        await client.SendAsync(ClientHello([ServerNameExtension("evil.test")]));

        Assert.Empty(await client.ReadToEndAsync());
        Assert.Equal(["example.com"], harness.Blocked);
        Assert.Equal(0, upstream.Accepted);
    }

    [Fact]
    public async Task Sni_that_differs_only_by_case_is_accepted()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();
        byte[] hello = ClientHello([ServerNameExtension("EXAMPLE.com")]);

        await client.SendAsync(Connect("example.com:443"));
        await client.ReadUntilAsync("\r\n\r\n");
        await client.SendAsync(hello);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length));
    }

    [Fact]
    public async Task Missing_sni_closes_the_tunnel()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect("example.com:443"));
        await client.ReadUntilAsync("\r\n\r\n");
        await client.SendAsync(ClientHello(null));

        Assert.Empty(await client.ReadToEndAsync());
        Assert.Equal(0, upstream.Accepted);
        Assert.Single(harness.Blocked);
    }

    [Fact]
    public async Task Non_tls_bytes_inside_the_tunnel_close_it()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect("example.com:80"));
        await client.ReadUntilAsync("\r\n\r\n");
        await client.SendAsync("GET / HTTP/1.1\r\nHost: evil.test\r\n\r\n");

        Assert.Empty(await client.ReadToEndAsync());
        Assert.Equal(0, upstream.Accepted);
    }

    [Fact]
    public async Task Plain_http_to_an_allowed_host_is_forwarded_without_proxy_headers()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = LoopbackServer.Http(received);
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(
            "GET http://Example.com/path/x?q=1 HTTP/1.1\r\nHost: example.com\r\nProxy-Connection: keep-alive\r\nproxy-authorization: Basic abc\r\nConnection: keep-alive\r\nX-Test: 1\r\n\r\n");
        string response = await client.ReadTextToEndAsync();
        string forwarded = await received.Task.WaitAsync(harness.Token);

        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.EndsWith("ok", response, StringComparison.Ordinal);
        Assert.StartsWith("GET /path/x?q=1 HTTP/1.1\r\nHost: example.com\r\n", forwarded, StringComparison.Ordinal);
        Assert.Contains("X-Test: 1\r\n", forwarded, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep-alive", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Blocked);
    }

    [Fact]
    public async Task Plain_http_body_is_forwarded()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = new LoopbackServer(async (socket, ct) =>
        {
            string head = await ProxyClient.ReadUntilAsync(socket, "\r\n\r\nbody", ct);
            received.TrySetResult(head);
            await socket.SendAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), System.Net.Sockets.SocketFlags.None, ct);
        });
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync("POST http://example.com HTTP/1.1\r\nHost: example.com\r\nContent-Length: 4\r\n\r\nbody");
        string response = await client.ReadTextToEndAsync();
        string forwarded = await received.Task.WaitAsync(harness.Token);

        Assert.StartsWith("HTTP/1.1 204", response, StringComparison.Ordinal);
        Assert.StartsWith("POST / HTTP/1.1\r\nHost: example.com\r\n", forwarded, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nbody", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_to_a_blocked_host_gets_a_403_page_and_never_reaches_the_upstream()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync("GET http://evil.test/ HTTP/1.1\r\nHost: evil.test\r\n\r\n");
        string response = await client.ReadTextToEndAsync();

        Assert.StartsWith("HTTP/1.1 403 Forbidden", response, StringComparison.Ordinal);
        Assert.Contains("text/html", response, StringComparison.Ordinal);
        Assert.Contains("evil.test", response, StringComparison.Ordinal);
        Assert.Equal(["evil.test"], harness.Blocked);
        Assert.Equal(0, upstream.Accepted);
    }

    [Fact]
    public async Task Plain_http_with_a_host_header_that_differs_from_the_uri_is_blocked()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\nHost: evil.test\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", await client.ReadTextToEndAsync(), StringComparison.Ordinal);
        Assert.Equal(0, upstream.Accepted);
    }

    [Theory]
    [InlineData("GET http://user@example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n", "400")]
    [InlineData("GET https://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n", "400")]
    [InlineData("GET http://127.0.0.1/ HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", "403")]
    [InlineData("GET http://example.com:8080/ HTTP/1.1\r\nHost: example.com:8080\r\n\r\n", "403")]
    [InlineData("garbage\r\n\r\n", "400")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\nHost: example.com\r\n\r\n", "400")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n folded: x\r\n\r\n", "400")]
    [InlineData("GET http://example.com/ HTTP/2.0\r\nHost: example.com\r\n\r\n", "400")]
    [InlineData("GET /nope HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", "404")]
    public async Task Malformed_or_unsupported_requests_get_an_error_status(string request, string status)
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(request);

        Assert.StartsWith("HTTP/1.1 " + status, await client.ReadTextToEndAsync(), StringComparison.Ordinal);
        Assert.Equal(0, upstream.Accepted);
    }

    [Fact]
    public async Task Unreachable_upstream_gives_502_for_plain_http()
    {
        await using var harness = ProxyHarness.Start(null);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 502", await client.ReadTextToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pac_file_is_served_for_direct_requests()
    {
        await using var harness = ProxyHarness.Start(null);
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync("GET /proxy.pac HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        string response = await client.ReadTextToEndAsync();

        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/x-ns-proxy-autoconfig\r\n", response, StringComparison.Ordinal);
        Assert.EndsWith(PacFile.Generate(harness.Port), response, StringComparison.Ordinal);
        Assert.Contains($"PROXY 127.0.0.1:{harness.Port}; DIRECT", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_header_block_is_rejected_with_431()
    {
        await using var upstream = LoopbackServer.Echo();
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();
        string prefix = "GET http://example.com/ HTTP/1.1\r\nX-Fill: ";

        // Exactly the limit and no terminator: the proxy has consumed everything it will ever read.
        await client.SendAsync(prefix + new string('a', 16 * 1024 - prefix.Length));

        Assert.StartsWith("HTTP/1.1 431", await client.ReadTextToEndAsync(), StringComparison.Ordinal);
        Assert.Equal(0, upstream.Accepted);
    }

    [Fact]
    public async Task Header_block_just_below_the_limit_is_accepted()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = LoopbackServer.Http(received);
        await using var harness = ProxyHarness.Start(upstream);
        using ProxyClient client = await harness.ConnectAsync();
        string prefix = "GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\nX-Fill: ";
        string suffix = "\r\n\r\n";

        await client.SendAsync(prefix + new string('a', 16 * 1024 - prefix.Length - suffix.Length) + suffix);

        Assert.StartsWith("HTTP/1.1 200", await client.ReadTextToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connections_above_the_limit_are_closed_immediately()
    {
        await using var harness = ProxyHarness.Start(null, new WebFilterProxyOptions { MaxConnections = 1 });
        using ProxyClient first = await harness.ConnectAsync();
        using ProxyClient second = await harness.ConnectAsync();

        Assert.Empty(await second.ReadToEndAsync());
    }

    [Fact]
    public async Task Throwing_event_handlers_do_not_break_the_proxy()
    {
        await using var harness = ProxyHarness.Start(null);
        harness.Proxy.RequestBlocked += (_, _) => throw new InvalidOperationException("boom");
        using ProxyClient client = await harness.ConnectAsync();

        await client.SendAsync(Connect("evil.test:443"));

        Assert.StartsWith("HTTP/1.1 403", await client.ReadTextToEndAsync(), StringComparison.Ordinal);
        Assert.Equal(["evil.test"], harness.Blocked);
    }

    [Fact]
    public async Task Stop_closes_open_connections_and_cannot_restart_twice()
    {
        await using var harness = ProxyHarness.Start(null);
        using ProxyClient idle = await harness.ConnectAsync();

        Assert.Throws<InvalidOperationException>(() => harness.Proxy.Start());
        await harness.Proxy.StopAsync();

        Assert.Empty(await idle.ReadToEndAsync());
        await harness.Proxy.StopAsync();
    }
}
