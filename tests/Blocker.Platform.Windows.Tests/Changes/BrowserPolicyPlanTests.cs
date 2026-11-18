using System.Text.Json;
using Blocker.Platform.Windows.Changes;
using Microsoft.Win32;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class BrowserPolicyPlanTests
{
    private static readonly string[] ChromiumRoots =
    [
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
    ];

    private static IReadOnlyDictionary<string, PolicyValue> Plan(int port = 18080) =>
        BrowserPolicyPlan.Create(port).ToDictionary(e => e.Target, e => e.Desired);

    [Fact]
    public void Plan_has_exactly_the_expected_entries()
    {
        IReadOnlyDictionary<string, PolicyValue> plan = Plan();

        Assert.Equal(15, plan.Count);
        Assert.Equal(15, BrowserPolicyPlan.Create(18080).Count); // no duplicate targets
    }

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Edge")]
    [InlineData(@"SOFTWARE\Policies\BraveSoftware\Brave")]
    public void Chromium_family_gets_pac_proxy_doh_off_and_no_quic(string root)
    {
        IReadOnlyDictionary<string, PolicyValue> plan = Plan(12345);

        PolicyValue proxy = plan[$"{root}|ProxySettings"];
        Assert.Equal(RegistryValueKind.String, proxy.Kind);
        using JsonDocument settings = JsonDocument.Parse(proxy.Text!);
        Assert.Equal("pac_script", settings.RootElement.GetProperty("ProxyMode").GetString());
        Assert.Equal("http://127.0.0.1:12345/proxy.pac", settings.RootElement.GetProperty("ProxyPacUrl").GetString());
        Assert.Equal("""{"ProxyMode":"pac_script","ProxyPacUrl":"http://127.0.0.1:12345/proxy.pac"}""", proxy.Text);
        Assert.Equal("off", plan[$"{root}|DnsOverHttpsMode"].Text);
        Assert.Equal(RegistryValueKind.DWord, plan[$"{root}|QuicAllowed"].Kind);
        Assert.Equal(0, plan[$"{root}|QuicAllowed"].Number);
        Assert.DoesNotContain($"{root}|BuiltInDnsClientEnabled", plan.Keys);
    }

    [Fact]
    public void Firefox_uses_its_registry_policy_layout()
    {
        IReadOnlyDictionary<string, PolicyValue> plan = Plan(12345);
        const string root = @"SOFTWARE\Policies\Mozilla\Firefox";

        Assert.Equal("autoConfig", plan[$@"{root}\Proxy|Mode"].Text);
        Assert.Equal("http://127.0.0.1:12345/proxy.pac", plan[$@"{root}\Proxy|AutoConfigURL"].Text);
        Assert.Equal(1, plan[$@"{root}\Proxy|Locked"].Number);
        Assert.Equal(0, plan[$@"{root}\DNSOverHTTPS|Enabled"].Number);
        Assert.Equal(1, plan[$@"{root}\DNSOverHTTPS|Locked"].Number);
        Assert.Equal(0, plan[$@"{root}\Preferences|network.http.http3.enable"].Number);
    }

    [Fact]
    public async Task Every_target_is_accepted_by_the_handler_and_uses_valid_json()
    {
        var store = new FakePolicyStore();
        var handler = new RegistryPolicyChangeHandler("install-1", store, RegistryPolicyChangeHandler.DefaultRoots);

        foreach (BrowserPolicyEntry entry in BrowserPolicyPlan.Create(8080))
        {
            await handler.ApplyAsync(entry.Target, entry.DesiredStateJson, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, store.Get(@"SOFTWARE\Policies\Mozilla\Firefox\Proxy", "Locked")!.Number);
        Assert.Equal("off", store.Get(ChromiumRoots[1], "DnsOverHttpsMode")!.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Invalid_ports_are_refused(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserPolicyPlan.Create(port));

    [Fact]
    public void Port_boundaries_are_accepted()
    {
        Assert.Contains("127.0.0.1:1/proxy.pac", Plan(1)[$@"{ChromiumRoots[0]}|ProxySettings"].Text);
        Assert.Contains("127.0.0.1:65535/proxy.pac", Plan(65535)[$@"{ChromiumRoots[0]}|ProxySettings"].Text);
    }
}
