using System.Net;

namespace Blocker.Core.WebFilter;

/// <summary>Resolves the upstream endpoints for <c>host:port</c>; tests substitute loopback endpoints.</summary>
/// <param name="host">Normalized host name.</param>
/// <param name="port">Requested port (80 or 443).</param>
/// <param name="cancellationToken">Cancellation.</param>
public delegate ValueTask<IReadOnlyList<IPEndPoint>> UpstreamResolver(string host, int port, CancellationToken cancellationToken);

/// <summary>Limits and settings of <see cref="WebFilterProxy"/>.</summary>
public sealed record WebFilterProxyOptions
{
    /// <summary>Loopback port; 0 picks an ephemeral one.</summary>
    public int Port { get; init; }

    /// <summary>Maximum request head size.</summary>
    public int MaxHeaderBytes { get; init; } = 16 * 1024;

    /// <summary>Connections above this are closed immediately.</summary>
    public int MaxConnections { get; init; } = 512;

    /// <summary>Close a connection after this long without traffic in either direction.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Upstream connect budget.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Data of <see cref="WebFilterProxy.RequestBlocked"/>.</summary>
public sealed class RequestBlockedEventArgs(string host) : EventArgs
{
    /// <summary>Requested host as presented by the client (length-capped).</summary>
    public string Host { get; } = host;
}
