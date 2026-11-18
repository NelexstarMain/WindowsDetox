using System.Globalization;

namespace Blocker.Core.WebFilter;

/// <summary>Proxy auto-config script: loopback and plain host names go direct, everything else through the local proxy, falling back to DIRECT if it is gone.</summary>
public static class PacFile
{
    /// <summary>Content type browsers expect for a PAC file.</summary>
    public const string ContentType = "application/x-ns-proxy-autoconfig";

    /// <summary>Generates the script for a proxy on 127.0.0.1:<paramref name="proxyPort"/>.</summary>
    public static string Generate(int proxyPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(proxyPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(proxyPort, 65535);
        return string.Create(CultureInfo.InvariantCulture, $$"""
            function FindProxyForURL(url, host) {
              if (isPlainHostName(host) || host === "localhost" || shExpMatch(host, "*.localhost") || host === "[::1]" || host === "::1" || /^127(\.[0-9]{1,3}){3}$/.test(host)) {
                return "DIRECT";
              }
              return "PROXY 127.0.0.1:{{proxyPort}}; DIRECT";
            }

            """);
    }
}
