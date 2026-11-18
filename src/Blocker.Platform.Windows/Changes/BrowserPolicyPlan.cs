namespace Blocker.Platform.Windows.Changes;

/// <summary>One policy value to journal through <see cref="RegistryPolicyChangeHandler"/>.</summary>
/// <param name="Target"><c>"&lt;keyPath&gt;|&lt;valueName&gt;"</c>.</param>
/// <param name="Desired">Value to write.</param>
internal sealed record BrowserPolicyEntry(string Target, PolicyValue Desired)
{
    /// <summary>The desired state as the JSON the handler expects.</summary>
    public string DesiredStateJson => ChangeGuard.ToJson(Desired, ChangesJsonContext.Default.PolicyValue);
}

/// <summary>
/// The browser policy set that routes browsers through the local PAC proxy and disables DoH and QUIC.
/// Chromium family (Chrome, Edge, Brave): <c>ProxySettings</c> (pac_script), <c>DnsOverHttpsMode=off</c>, <c>QuicAllowed=0</c>
/// (<c>BuiltInDnsClientEnabled</c> is left alone). Firefox uses its registry policy layout: <c>Proxy\{Mode,AutoConfigURL,Locked}</c>,
/// <c>DNSOverHTTPS\{Enabled,Locked}</c> and, best effort, <c>Preferences\network.http.http3.enable=0</c>: Firefox only honours
/// preferences on its policy allow-list and ignores others, so the WFP UDP 443 block is the real QUIC enforcement for Firefox.
/// </summary>
internal static class BrowserPolicyPlan
{
    private static readonly string[] ChromiumRoots =
    [
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
    ];

    private const string FirefoxRoot = @"SOFTWARE\Policies\Mozilla\Firefox";

    /// <summary>Builds the policy list for a PAC served on <c>127.0.0.1:<paramref name="proxyPort"/></c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The port is not in 1..65535.</exception>
    public static IReadOnlyList<BrowserPolicyEntry> Create(int proxyPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(proxyPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(proxyPort, 65535);

        string pacUrl = $"http://127.0.0.1:{proxyPort}/proxy.pac";
        string proxySettings = $$"""{"ProxyMode":"pac_script","ProxyPacUrl":"{{pacUrl}}"}""";
        List<BrowserPolicyEntry> entries = [];

        foreach (string root in ChromiumRoots)
        {
            entries.Add(new($"{root}|ProxySettings", PolicyValue.String(proxySettings)));
            entries.Add(new($"{root}|DnsOverHttpsMode", PolicyValue.String("off")));
            entries.Add(new($"{root}|QuicAllowed", PolicyValue.DWord(0)));
        }

        entries.Add(new($@"{FirefoxRoot}\Proxy|Mode", PolicyValue.String("autoConfig")));
        entries.Add(new($@"{FirefoxRoot}\Proxy|AutoConfigURL", PolicyValue.String(pacUrl)));
        entries.Add(new($@"{FirefoxRoot}\Proxy|Locked", PolicyValue.DWord(1)));
        entries.Add(new($@"{FirefoxRoot}\DNSOverHTTPS|Enabled", PolicyValue.DWord(0)));
        entries.Add(new($@"{FirefoxRoot}\DNSOverHTTPS|Locked", PolicyValue.DWord(1)));
        entries.Add(new($@"{FirefoxRoot}\Preferences|network.http.http3.enable", PolicyValue.DWord(0)));
        return entries;
    }
}
