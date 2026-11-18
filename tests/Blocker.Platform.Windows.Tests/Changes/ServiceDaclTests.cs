using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class ServiceDaclTests
{
    private const string Default = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;AU)";
    private const string Service = "BlockerSvc";

    private readonly FakeServiceSecurity _security = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private ServiceDaclChangeHandler Create(Dictionary<string, string>? defaults = null) => new(_security, defaults);

    [Fact]
    public void Restricted_sddl_is_exactly_the_documented_string() =>
        Assert.Equal(
            "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLORC;;;BA)(A;;CCLCSWRPLORC;;;IU)(A;;CCLCSWRPLORC;;;AU)",
            ServiceSddl.BuildRestricted());

    [Fact]
    public void Restricted_sddl_grants_non_system_trustees_only_query_interrogate_and_start()
    {
        string[] aces = ServiceSddl.BuildRestricted()[2..].Split(')', StringSplitOptions.RemoveEmptyEntries).Select(a => a.TrimStart('(')).ToArray();

        Assert.Equal(4, aces.Length);
        foreach (string[] fields in aces.Select(a => a.Split(';')).Where(f => f[5] != "SY"))
        {
            string[] rights = [.. Enumerable.Range(0, fields[2].Length / 2).Select(i => fields[2].Substring(i * 2, 2))];
            Assert.Equal("A", fields[0]);
            Assert.Equal(["CC", "LC", "SW", "RP", "LO", "RC"], rights);
            foreach (string forbidden in new[] { "WP", "SD", "WD", "WO", "DC", "DT", "CR", "GA", "GW" })
            {
                Assert.DoesNotContain(forbidden, rights); // stop, delete, write DAC/owner, change config, pause, user control
            }
        }

        Assert.Contains(ServiceSddl.SystemAce, ServiceSddl.BuildRestricted());
    }

    [Theory]
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLORC;;;BA)", true)]
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)", true)]
    [InlineData("D:(A;;CCLCSWRPLORC;;;BA)", false)] // SYSTEM missing
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPWPLORC;;;BA)", false)] // stop
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)", false)] // full control
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;0x2009d;;;BA)", false)] // hex masks are not inspected
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(D;;CCLC;;;BA)", false)] // deny ACE
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;GA;;;WD)", false)]
    [InlineData("O:SYD:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)", false)]
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)S:(AU;;CC;;;WD)", false)]
    [InlineData("(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)", false)]
    [InlineData("", false)]
    public void IsSafe_whitelists_rights(string sddl, bool expected) => Assert.Equal(expected, ServiceSddl.IsSafe(sddl));

    [Fact]
    public async Task Capture_returns_null_for_missing_service_and_json_otherwise()
    {
        Assert.Null(await Create().CaptureOriginalAsync(Service, _ct));
        _security.Sddl[Service] = Default;

        Assert.Equal($$"""{"sddl":"{{Default}}"}""", await Create().CaptureOriginalAsync(Service, _ct));
    }

    [Fact]
    public async Task Apply_sets_restricted_dacl_and_restore_returns_the_original()
    {
        _security.Sddl[Service] = Default;
        ServiceDaclChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Service, _ct);

        await handler.ApplyAsync(Service, "{}", _ct);
        Assert.Equal(ServiceSddl.BuildRestricted(), _security.Sddl[Service]);
        await handler.RestoreAsync(Service, original, _ct);
        await handler.RestoreAsync(Service, original, _ct);

        Assert.Equal(Default, _security.Sddl[Service]);
    }

    [Fact]
    public async Task Apply_refuses_unsafe_custom_dacl_and_missing_service()
    {
        _security.Sddl[Service] = Default;
        ServiceDaclChangeHandler handler = Create();

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.ApplyAsync(Service, """{"sddl":"D:(A;;GA;;;WD)"}""", _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.ApplyAsync("Missing", "{}", _ct));
        Assert.Equal(Default, _security.Sddl[Service]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("a\nb")]
    public void Invalid_service_names_are_refused(string name) =>
        Assert.Throws<ArgumentException>(() => { _ = Create().CaptureOriginalAsync(name, _ct); });

    [Fact]
    public async Task Restore_of_a_vanished_service_or_missing_original_is_a_no_op()
    {
        await Create().RestoreAsync("Missing", """{"sddl":"D:"}""", _ct);
        await Create().RestoreAsync(Service, null, _ct);

        Assert.Empty(_security.Sddl);
    }

    [Fact]
    public async Task Sweep_resets_only_services_that_still_carry_the_restricted_dacl()
    {
        _security.Sddl["A"] = ServiceSddl.BuildRestricted();
        _security.Sddl["B"] = Default; // untouched
        _security.Sddl["C"] = ServiceSddl.BuildRestricted();
        ServiceDaclChangeHandler handler = Create(new() { ["A"] = "D:defaultA", ["B"] = "D:defaultB", ["Gone"] = "D:x" });

        int removed = await handler.SweepAsync("install-1", _ct);

        Assert.Equal(1, removed);
        Assert.Equal("D:defaultA", _security.Sddl["A"]);
        Assert.Equal(Default, _security.Sddl["B"]);
        Assert.Equal(ServiceSddl.BuildRestricted(), _security.Sddl["C"]); // no default known: left alone
    }

    [Fact]
    public async Task Sweep_without_defaults_is_a_no_op()
    {
        _security.Sddl["A"] = ServiceSddl.BuildRestricted();

        Assert.Equal(0, await Create().SweepAsync("install-1", _ct));
        Assert.Equal(ServiceSddl.BuildRestricted(), _security.Sddl["A"]);
    }

    [Fact]
    public void Kind_is_stable() => Assert.Equal("servicedacl", Create().Kind);
}
