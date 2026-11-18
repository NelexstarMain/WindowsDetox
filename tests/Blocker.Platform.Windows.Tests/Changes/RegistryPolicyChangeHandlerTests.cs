using System.Text.Json;
using Blocker.Platform.Windows.Changes;
using Microsoft.Win32;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class RegistryPolicyChangeHandlerTests
{
    private const string InstallId = "install-1";
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Marker = "BlockerOwned.";

    private readonly FakePolicyStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private RegistryPolicyChangeHandler Create() => new(InstallId, _store, RegistryPolicyChangeHandler.DefaultRoots);

    private static string Json(PolicyValue value) => JsonSerializer.Serialize(value, ChangesJsonContext.Default.PolicyValue);

    [Theory]
    [InlineData("nobar")]
    [InlineData("|value")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome|")]
    [InlineData(@"SOFTWARE\Policies\Evil|Value")]
    [InlineData(@"SOFTWARE\Policies\Google\ChromeEvil|Value")]
    [InlineData(@"SOFTWARE\Policies\Google|Value")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome\..\..\Evil|Value")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome\.\x|Value")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome\\x|Value")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome|BlockerOwned")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome|BlockerOwned.ProxySettings")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome|a\b")]
    public void Targets_outside_the_allowlist_or_malformed_are_refused(string target)
    {
        RegistryPolicyChangeHandler handler = Create();

        Assert.Throws<ArgumentException>(() => { _ = handler.CaptureOriginalAsync(target, _ct); });
        Assert.Throws<ArgumentException>(() => { _ = handler.ApplyAsync(target, Json(PolicyValue.DWord(1)), _ct); });
        Assert.Throws<ArgumentException>(() => { _ = handler.RestoreAsync(target, null, _ct); });
        Assert.False(_store.KeyExists(@"SOFTWARE\Policies\Evil"));
    }

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome|ProxySettings")]
    [InlineData(@"software\policies\microsoft\edge|QuicAllowed")]
    [InlineData(@"SOFTWARE\Policies\BraveSoftware\Brave|DnsOverHttpsMode")]
    [InlineData(@"SOFTWARE\Policies\Mozilla\Firefox\Proxy|Mode")]
    [InlineData(@"SOFTWARE\Policies\Mozilla\Firefox\Preferences|network.http.http3.enable")]
    public async Task Allowed_targets_are_accepted(string target) =>
        Assert.NotNull(await Create().CaptureOriginalAsync(target, _ct));

    [Fact]
    public async Task Apply_writes_value_and_sibling_marker()
    {
        await Create().ApplyAsync($"{Chrome}|ProxySettings", Json(PolicyValue.String("{}")), _ct);
        await Create().ApplyAsync($"{Chrome}|QuicAllowed", Json(PolicyValue.DWord(0)), _ct);

        Assert.Equal("{}", _store.Get(Chrome, "ProxySettings")!.Text);
        Assert.Equal(InstallId, _store.Get(Chrome, Marker + "ProxySettings")!.Text);
        Assert.Equal(0, _store.Get(Chrome, "QuicAllowed")!.Number);
        Assert.Equal(RegistryValueKind.DWord, _store.Get(Chrome, "QuicAllowed")!.Kind);
        Assert.Equal(InstallId, _store.Get(Chrome, Marker + "QuicAllowed")!.Text);
    }

    [Theory]
    [InlineData("""{"kind":"String"}""")]
    [InlineData("""{"kind":"DWord"}""")]
    [InlineData("""{"kind":"DWord","number":-1}""")]
    [InlineData("""{"kind":"DWord","number":4294967296}""")]
    [InlineData("""{"kind":"Binary","bytes":"AAE="}""")]
    [InlineData("""{"kind":"Bogus","text":"x"}""")]
    [InlineData("[]")]
    public async Task Invalid_desired_values_are_refused_without_writing(string desired)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Create().ApplyAsync($"{Chrome}|X", desired, _ct));

        Assert.False(_store.KeyExists(Chrome));
    }

    [Fact]
    public async Task Absent_value_and_key_chain_are_removed_on_restore()
    {
        _store.SeedKey(@"SOFTWARE\Policies");
        RegistryPolicyChangeHandler handler = Create();
        string target = @"SOFTWARE\Policies\Mozilla\Firefox\Proxy|Mode";
        string? original = await handler.CaptureOriginalAsync(target, _ct);
        Assert.Equal("""{"existingDepth":2}""", original);

        await handler.ApplyAsync(target, Json(PolicyValue.String("autoConfig")), _ct);
        Assert.True(_store.KeyExists(@"SOFTWARE\Policies\Mozilla\Firefox\Proxy"));
        await handler.RestoreAsync(target, original, _ct);

        Assert.False(_store.KeyExists(@"SOFTWARE\Policies\Mozilla"));
        Assert.True(_store.KeyExists(@"SOFTWARE\Policies"));
    }

    [Fact]
    public async Task Pruning_stops_at_keys_that_still_hold_other_values()
    {
        RegistryPolicyChangeHandler handler = Create();
        string a = $"{Chrome}|A";
        string b = $"{Chrome}|B";
        string? originalA = await handler.CaptureOriginalAsync(a, _ct);
        await handler.ApplyAsync(a, Json(PolicyValue.DWord(1)), _ct);
        string? originalB = await handler.CaptureOriginalAsync(b, _ct);
        await handler.ApplyAsync(b, Json(PolicyValue.DWord(2)), _ct);

        await handler.RestoreAsync(b, originalB, _ct);
        Assert.NotNull(_store.Get(Chrome, "A"));
        await handler.RestoreAsync(a, originalA, _ct);

        Assert.False(_store.KeyExists(@"SOFTWARE\Policies"));
    }

    [Fact]
    public async Task Existing_values_of_any_kind_are_restored_exactly()
    {
        PolicyValue[] priors =
        [
            PolicyValue.String("old"),
            PolicyValue.DWord(uint.MaxValue),
            PolicyValues.Multi("a", "b"),
            new PolicyValue(RegistryValueKind.Binary, Bytes: [1, 2, 3]),
            new PolicyValue(RegistryValueKind.ExpandString, Text: "%TEMP%\\x"),
            new PolicyValue(RegistryValueKind.QWord, Number: 1L << 40),
        ];

        RegistryPolicyChangeHandler handler = Create();
        foreach ((PolicyValue prior, int i) in priors.Select((p, i) => (p, i)))
        {
            string target = $"{Chrome}|V{i}";
            _store.Seed(Chrome, $"V{i}", prior);
            string? original = await handler.CaptureOriginalAsync(target, _ct);

            await handler.ApplyAsync(target, Json(PolicyValue.String("new")), _ct);
            await handler.RestoreAsync(target, original, _ct);

            Assert.Equal(JsonSerializer.Serialize(prior, ChangesJsonContext.Default.PolicyValue), Json(_store.Get(Chrome, $"V{i}")!));
            Assert.Null(_store.Get(Chrome, Marker + $"V{i}"));
        }
    }

    [Fact]
    public async Task Restore_is_idempotent_and_leaves_unowned_values_alone()
    {
        RegistryPolicyChangeHandler handler = Create();
        string target = $"{Chrome}|QuicAllowed";
        _store.Seed(Chrome, "QuicAllowed", PolicyValue.DWord(1));
        string? original = await handler.CaptureOriginalAsync(target, _ct);
        await handler.ApplyAsync(target, Json(PolicyValue.DWord(0)), _ct);
        await handler.RestoreAsync(target, original, _ct);

        _store.Seed(Chrome, "QuicAllowed", PolicyValue.DWord(7)); // admin edits it afterwards
        await handler.RestoreAsync(target, original, _ct);
        await handler.RestoreAsync(target, null, _ct);

        Assert.Equal(7, _store.Get(Chrome, "QuicAllowed")!.Number);
    }

    [Fact]
    public async Task Stale_marker_at_capture_means_the_visible_value_is_not_original()
    {
        _store.Seed(Chrome, "QuicAllowed", PolicyValue.DWord(0));
        _store.Seed(Chrome, Marker + "QuicAllowed", PolicyValue.String("dead-install"));

        string? original = await Create().CaptureOriginalAsync($"{Chrome}|QuicAllowed", _ct);

        Assert.Equal("""{"existingDepth":4}""", original);
    }

    [Fact]
    public async Task Sweep_removes_owned_values_prunes_empty_keys_but_keeps_the_root_and_other_installs()
    {
        RegistryPolicyChangeHandler handler = Create();
        await handler.ApplyAsync($@"{Chrome}\Sub|X", Json(PolicyValue.DWord(1)), _ct);
        await handler.ApplyAsync($"{Chrome}|Y", Json(PolicyValue.DWord(1)), _ct);
        _store.Seed(@"SOFTWARE\Policies\Microsoft\Edge", "Z", PolicyValue.DWord(5));
        _store.Seed(@"SOFTWARE\Policies\Microsoft\Edge", Marker + "Z", PolicyValue.String("other-install"));

        int removed = await handler.SweepAsync(InstallId, _ct);

        Assert.Equal(2, removed);
        Assert.False(_store.KeyExists($@"{Chrome}\Sub"));
        Assert.True(_store.KeyExists(Chrome)); // allowlist root itself is never deleted
        Assert.Null(_store.Get(Chrome, "Y"));
        Assert.Equal(5, _store.Get(@"SOFTWARE\Policies\Microsoft\Edge", "Z")!.Number);
        Assert.Equal(0, await handler.SweepAsync(InstallId, _ct));
    }

    [Fact]
    public void Policy_values_round_trip_through_source_generated_json()
    {
        foreach (PolicyValue value in new[] { PolicyValue.String("x"), PolicyValue.DWord(5), PolicyValues.Multi("a"), new PolicyValue(RegistryValueKind.Binary, Bytes: [9]) })
        {
            PolicyValue? back = JsonSerializer.Deserialize(Json(value), ChangesJsonContext.Default.PolicyValue);

            Assert.Equal(Json(value), Json(back!));
        }

        Assert.Equal("""{"kind":"DWord","number":5}""", Json(PolicyValue.DWord(5)));
    }

    [Fact]
    public void Kind_is_stable() => Assert.Equal("regpolicy", Create().Kind);
}
