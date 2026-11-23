using System.Text.Json;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class IfeoChangeHandlerTests
{
    private const string InstallId = "install-1";
    private const string Image = "chrome.exe";
    private const string DesiredJson = """{"stubPath":"C:\\Program Files\\Blocker\\stub.exe"}""";
    private const string ExpectedDebugger = "\"C:\\Program Files\\Blocker\\stub.exe\" --ifeo";

    private readonly FakeIfeoStore _store = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private const string OldDebugger = @"C:\Program Files\Old\old.exe";

    private IfeoChangeHandler Create(FakeIfeoStore? store = null) =>
        new(InstallId, ["explorer.exe", "csrss.exe"], store ?? _store, [@"C:\Program Files", @"C:\Windows\System32"]);

    [Theory]
    [InlineData("")]
    [InlineData(".exe")]
    [InlineData("chrome")]
    [InlineData("chrome.txt")]
    [InlineData(@"C:\x\chrome.exe")]
    [InlineData(@"..\chrome.exe")]
    [InlineData("a/b.exe")]
    [InlineData("a:b.exe")]
    [InlineData("a*.exe")]
    [InlineData("a..exe")]
    [InlineData("a .exe")]
    [InlineData(" a.exe")]
    [InlineData("a\0.exe")]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData("csrss.exe")]
    public void Invalid_or_critical_targets_are_refused_everywhere(string target)
    {
        IfeoChangeHandler handler = Create();

        // The handlers validate synchronously, before returning a ValueTask.
        Assert.ThrowsAny<ArgumentException>(() => { _ = handler.CaptureOriginalAsync(target, _ct); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = handler.ApplyAsync(target, DesiredJson, _ct); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = handler.RestoreAsync(target, null, _ct); });
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("my app.exe")]
    [InlineData("Blocker-Test_1.EXE")]
    public void Simple_exe_names_are_accepted(string target) =>
        Assert.Equal(target, IfeoChangeHandler.ValidateTarget(target, new HashSet<string>()));

    [Fact]
    public async Task Apply_writes_debugger_and_marker_in_both_views()
    {
        IfeoChangeHandler handler = Create();

        await handler.ApplyAsync(Image, DesiredJson, _ct);

        foreach (IfeoView view in new[] { IfeoView.Native, IfeoView.Wow64 })
        {
            Dictionary<string, string> values = Assert.IsType<Dictionary<string, string>>(_store.Values(view, Image));
            Assert.Equal(ExpectedDebugger, values["Debugger"]);
            Assert.Equal(InstallId, values["BlockerOwned"]);
        }
    }

    [Fact]
    public async Task Apply_rejects_relative_stub_and_invalid_json()
    {
        IfeoChangeHandler handler = Create();

        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.ApplyAsync(Image, """{"stubPath":"stub.exe"}""", _ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.ApplyAsync(Image, """{"stubPath":"C:\\x\\stub.dll"}""", _ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.ApplyAsync(Image, "{not json", _ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await handler.ApplyAsync(Image, "null", _ct));
        Assert.Null(_store.Values(IfeoView.Native, Image));
    }

    [Fact]
    public async Task Absent_key_is_created_and_removed_again()
    {
        IfeoChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Image, _ct);
        Assert.NotNull(original);
        Assert.Equal("""{"native":{"keyExisted":false},"wow64":{"keyExisted":false}}""", original);

        await handler.ApplyAsync(Image, DesiredJson, _ct);
        await handler.RestoreAsync(Image, original, _ct);

        Assert.Null(_store.Values(IfeoView.Native, Image));
        Assert.Null(_store.Values(IfeoView.Wow64, Image));
    }

    [Fact]
    public async Task Prior_debugger_is_restored_and_other_values_survive()
    {
        _store.Seed(IfeoView.Native, Image, ("Debugger", OldDebugger), ("GlobalFlag", "512"));
        _store.Seed(IfeoView.Wow64, Image, ("GlobalFlag", "256"));
        IfeoChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Image, _ct);

        await handler.ApplyAsync(Image, DesiredJson, _ct);
        Assert.Equal("512", _store.Values(IfeoView.Native, Image)!["GlobalFlag"]);
        await handler.RestoreAsync(Image, original, _ct);

        Assert.Equal(new Dictionary<string, string> { ["Debugger"] = OldDebugger, ["GlobalFlag"] = "512" }, _store.Values(IfeoView.Native, Image));
        Assert.Equal(new Dictionary<string, string> { ["GlobalFlag"] = "256" }, _store.Values(IfeoView.Wow64, Image));
    }

    [Theory]
    [InlineData(@"C:\Users\mallory\evil.exe")] // user-writable
    [InlineData(@"""C:\Users\mallory\evil.exe"" --ifeo")]
    [InlineData("evil.exe")] // relative: resolved through a search path
    [InlineData(@"C:\Program Files\..\Users\evil.exe")]
    [InlineData(@"C:\Program Files2\evil.exe")] // prefix of the directory name, not inside it
    [InlineData(@"\\server\share\evil.exe")]
    [InlineData("")]
    public async Task Restore_drops_a_recorded_debugger_outside_the_trusted_directories(string forged)
    {
        _store.Seed(IfeoView.Native, Image, ("Debugger", ExpectedDebugger), ("BlockerOwned", InstallId));
        string json = System.Text.Json.JsonSerializer.Serialize(new { native = new { keyExisted = true, debugger = forged }, wow64 = new { keyExisted = false, debugger = (string?)null } });

        await Create().RestoreAsync(Image, json, _ct);

        Assert.False(_store.Values(IfeoView.Native, Image)!.ContainsKey("Debugger")); // redirect gone, forged value not planted
        Assert.False(_store.Values(IfeoView.Native, Image)!.ContainsKey("BlockerOwned"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Old\old.exe", true)]
    [InlineData(@"""C:\Program Files\Old Tool\old.exe"" -p %ld -e %ld", true)]
    [InlineData(@"c:\windows\system32\vsjitdebugger.exe -p %ld", true)]
    [InlineData(@"C:\Windows\Temp\x.exe", false)]
    [InlineData(@"C:\Program Files\Old Tool\old.exe -p 1", true)] // an unquoted path is read up to ".exe", as CreateProcess does
    [InlineData(@"C:\Program", false)]
    public void Debugger_trust_is_decided_by_the_directory_of_the_program(string debugger, bool expected) =>
        Assert.Equal(expected, IfeoChangeHandler.IsDebuggerTrusted(debugger, [@"C:\Program Files", @"C:\Windows\System32"]));

    [Fact]
    public async Task Existing_empty_key_is_kept_after_restore()
    {
        _store.Seed(IfeoView.Native, Image);
        _store.Seed(IfeoView.Wow64, Image);
        IfeoChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Image, _ct);

        await handler.ApplyAsync(Image, DesiredJson, _ct);
        await handler.RestoreAsync(Image, original, _ct);

        Assert.Empty(_store.Values(IfeoView.Native, Image)!);
    }

    [Fact]
    public async Task Restore_is_idempotent_and_does_not_clobber_foreign_edits()
    {
        IfeoChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Image, _ct);
        await handler.ApplyAsync(Image, DesiredJson, _ct);
        await handler.RestoreAsync(Image, original, _ct);

        _store.Seed(IfeoView.Native, Image, ("Debugger", "someone-else.exe")); // third party after our restore
        await handler.RestoreAsync(Image, original, _ct);
        await handler.RestoreAsync(Image, null, _ct);

        Assert.Equal("someone-else.exe", _store.Values(IfeoView.Native, Image)!["Debugger"]);
    }

    [Fact]
    public async Task Restore_without_original_removes_only_owned_values()
    {
        await Create().ApplyAsync(Image, DesiredJson, _ct);

        await Create().RestoreAsync(Image, null, _ct);

        Assert.Null(_store.Values(IfeoView.Native, Image));
    }

    [Fact]
    public async Task Stale_marker_at_capture_is_not_recorded_as_original()
    {
        _store.Seed(IfeoView.Native, Image, ("Debugger", "\"stub\" --ifeo"), ("BlockerOwned", "dead-install"));
        _store.Seed(IfeoView.Wow64, Image);

        using JsonDocument json = JsonDocument.Parse((await Create().CaptureOriginalAsync(Image, _ct))!);

        JsonElement native = json.RootElement.GetProperty("native");
        Assert.True(native.GetProperty("keyExisted").GetBoolean());
        Assert.False(native.TryGetProperty("debugger", out _));
    }

    [Fact]
    public async Task Shared_key_between_views_round_trips()
    {
        var shared = new FakeIfeoStore { SharedKey = true };
        shared.Seed(IfeoView.Native, Image, ("Debugger", OldDebugger));
        IfeoChangeHandler handler = Create(shared);
        string? original = await handler.CaptureOriginalAsync(Image, _ct);

        await handler.ApplyAsync(Image, DesiredJson, _ct);
        await handler.RestoreAsync(Image, original, _ct);

        Assert.Equal(new Dictionary<string, string> { ["Debugger"] = OldDebugger }, shared.Values(IfeoView.Native, Image));
    }

    [Fact]
    public async Task Sweep_removes_only_keys_owned_by_the_install_id()
    {
        IfeoChangeHandler handler = Create();
        await handler.ApplyAsync("a.exe", DesiredJson, _ct);
        await handler.ApplyAsync("b.exe", DesiredJson, _ct);
        _store.Seed(IfeoView.Native, "c.exe", ("Debugger", "x"), ("BlockerOwned", "other-install"));
        _store.Seed(IfeoView.Native, "d.exe", ("Debugger", "y"));

        int removed = await handler.SweepAsync(InstallId, _ct);

        Assert.Equal(4, removed); // two images in two views
        Assert.Null(_store.Values(IfeoView.Native, "a.exe"));
        Assert.Null(_store.Values(IfeoView.Wow64, "b.exe"));
        Assert.Equal("x", _store.Values(IfeoView.Native, "c.exe")!["Debugger"]);
        Assert.Equal("y", _store.Values(IfeoView.Native, "d.exe")!["Debugger"]);
        Assert.Equal(0, await handler.SweepAsync(InstallId, _ct));
    }

    [Fact]
    public void Kind_is_stable() => Assert.Equal("ifeo", Create().Kind);
}
