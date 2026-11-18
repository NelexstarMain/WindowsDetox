using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class WfpTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static IEnumerable<ushort> Ports(WfpFilterSpec spec) => spec.Conditions.OfType<WfpRemotePortCondition>().Select(c => c.Port);

    private static byte Protocol(WfpFilterSpec spec) => spec.Conditions.OfType<WfpProtocolCondition>().Single().Protocol;

    [Fact]
    public void Plan_has_four_filters_per_browser_and_address_family_and_collapses_duplicates()
    {
        WfpFilterPlan plan = WfpFilterPlan.Create([Chrome, Firefox, Chrome.ToUpperInvariant()]);

        Assert.Equal(16, plan.Filters.Count);
        Assert.Equal(8, plan.Filters.Count(f => f.AppPath == Chrome));
        Assert.Equal(8, plan.Filters.Count(f => f.AppPath == Firefox));
        Assert.Equal(plan.Filters.Count, plan.Filters.Select(f => f.Name).Distinct().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Per_layer_filters_permit_loopback_and_block_web_quic_and_dot(bool ipv4)
    {
        WfpLayer layer = ipv4 ? WfpLayer.ConnectV4 : WfpLayer.ConnectV6;
        WfpFilterSpec[] filters = [.. WfpFilterPlan.Create([Chrome]).Filters.Where(f => f.Layer == layer)];

        Assert.Equal(4, filters.Length);

        WfpFilterSpec permit = filters[0];
        Assert.Equal(WfpAction.Permit, permit.Action);
        WfpRemoteSubnetCondition loopback = Assert.IsType<WfpRemoteSubnetCondition>(Assert.Single(permit.Conditions));
        if (layer == WfpLayer.ConnectV4)
        {
            Assert.Equal(new byte[] { 127, 0, 0, 0 }, loopback.Address);
            Assert.Equal(8, loopback.PrefixLength);
        }
        else
        {
            Assert.Equal(new byte[16] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, loopback.Address);
            Assert.Equal(128, loopback.PrefixLength);
        }

        AssertBlock(filters[1], WfpFilterPlan.Tcp, 80, 443);
        AssertBlock(filters[2], WfpFilterPlan.Udp, 443, 853);
        AssertBlock(filters[3], WfpFilterPlan.Tcp, 853);
    }

    private static void AssertBlock(WfpFilterSpec spec, byte protocol, params ushort[] ports)
    {
        Assert.Equal(WfpAction.Block, spec.Action);
        Assert.Equal(protocol, Protocol(spec));
        Assert.Equal(ports, Ports(spec).Order().ToArray());
    }

    [Fact]
    public void Loopback_permit_outweighs_every_block_and_weights_fit_the_uint8_range()
    {
        WfpFilterPlan plan = WfpFilterPlan.Create([Chrome]);

        byte permit = plan.Filters.Where(f => f.Action == WfpAction.Permit).Min(f => f.Weight);
        byte block = plan.Filters.Where(f => f.Action == WfpAction.Block).Max(f => f.Weight);
        Assert.True(permit > block);
        Assert.All(plan.Filters, f => Assert.InRange(f.Weight, 0, 15));
        Assert.All(plan.Filters, f => Assert.Equal(Chrome, f.AppPath));
    }

    [Fact]
    public void Empty_input_gives_an_empty_plan() => Assert.Empty(WfpFilterPlan.Create([]).Filters);

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData(@"C:\x\chrome.dll")]
    [InlineData(@"C:\x\..\chrome.exe")]
    [InlineData(@"\\server\share\chrome.exe")]
    [InlineData("")]
    public void Invalid_executables_are_refused(string exe) =>
        Assert.Throws<ArgumentException>(() => WfpFilterPlan.Create([exe]));

    [Fact]
    public async Task Enable_installs_the_plan_and_disable_disposes_the_session()
    {
        List<string> log = [];
        WfpFilterPlan? seen = null;
        var firewall = new WfpBrowserFirewall(plan =>
        {
            seen = plan;
            log.Add("install");
            return new FakeWfpSession(log, "s1");
        });
        Assert.False(firewall.IsEnabled);

        await firewall.EnableAsync([Chrome], _ct);
        Assert.True(firewall.IsEnabled);
        Assert.Equal(8, seen!.Filters.Count);

        await firewall.DisableAsync(_ct);
        await firewall.DisableAsync(_ct);

        Assert.False(firewall.IsEnabled);
        Assert.Equal(["install", "dispose s1"], log);
    }

    [Fact]
    public async Task Re_enable_installs_the_new_session_before_closing_the_old_one()
    {
        List<string> log = [];
        int n = 0;
        var firewall = new WfpBrowserFirewall(_ =>
        {
            string name = $"s{++n}";
            log.Add($"install {name}");
            return new FakeWfpSession(log, name);
        });

        await firewall.EnableAsync([Chrome], _ct);
        await firewall.EnableAsync([Chrome, Firefox], _ct);
        await firewall.DisposeAsync();

        Assert.Equal(["install s1", "install s2", "dispose s1", "dispose s2"], log);
        Assert.False(firewall.IsEnabled);
    }

    [Fact]
    public async Task Empty_list_disables_and_invalid_input_keeps_the_running_session()
    {
        List<string> log = [];
        var firewall = new WfpBrowserFirewall(_ => new FakeWfpSession(log, "s1"));
        await firewall.EnableAsync([Chrome], _ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await firewall.EnableAsync(["relative.exe"], _ct));
        Assert.True(firewall.IsEnabled);
        Assert.Empty(log);

        await firewall.EnableAsync([], _ct);
        Assert.False(firewall.IsEnabled);
        Assert.Equal(["dispose s1"], log);
    }
}
