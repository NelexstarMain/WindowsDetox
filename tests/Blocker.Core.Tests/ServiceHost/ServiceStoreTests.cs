using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Security;
using Blocker.Core.ServiceHost;
using Blocker.Core.Session;
using Blocker.Core.Tests.Security;
using Blocker.Sdk.Session;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class ServiceStoreTests
{
    private static readonly string Root = MockUnixSupport.Path(@"C:\ProgramData\WindowsDetox");

    private readonly MockFileSystem _fs = new();
    private readonly SecureFileStore _store;

    public ServiceStoreTests()
    {
        var protector = new FakeSecretProtector();
        _store = new SecureFileStore(_fs, protector, new HmacKeyStore(_fs, protector, _fs.Path.Combine(Root, "hmac.key")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Path(string name) => _fs.Path.Combine(Root, name);

    private void Damage(string path) => _fs.File.WriteAllBytes(path, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);

    [Fact]
    public async Task The_session_store_round_trips_a_full_session_and_clears_it()
    {
        var store = new SecureSessionStore(_store, Path("session.bin"));
        var config = new SessionConfig(TimeSpan.FromHours(2), 3, TimeSpan.FromMinutes(5), true, "UTC");
        var session = new PersistedSession(
            config,
            new SessionClockState(Guid.NewGuid(), TimeSpan.FromHours(2), 1234, 5678, 99, new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero)),
            new EmergencyPassLedgerState(config, "UTC", new DateOnly(2026, 11, 17), 2, true, 1500, 5678),
            new BreakGlassHash([1, 2, 3], [4, 5, 6], 1000),
            SessionPhase.EmergencyPass);

        Assert.Null(await store.LoadAsync(Ct));
        await store.SaveAsync(session, Ct);
        PersistedSession? loaded = await store.LoadAsync(Ct);

        Assert.NotNull(loaded);
        Assert.Equal(session.Config, loaded.Config);
        Assert.Equal(session.Clock, loaded.Clock);
        Assert.Equal(session.Ledger, loaded.Ledger);
        Assert.Equal(session.Phase, loaded.Phase);
        Assert.Equal(session.BreakGlass.Salt, loaded.BreakGlass.Salt);
        Assert.Equal(session.BreakGlass.Hash, loaded.BreakGlass.Hash);
        await store.ClearAsync(Ct);
        Assert.Null(await store.LoadAsync(Ct));
        Assert.False(_fs.File.Exists(Path("session.bin")));
    }

    [Fact]
    public async Task A_damaged_session_file_is_reported_as_tampering()
    {
        var store = new SecureSessionStore(_store, Path("session.bin"));
        _fs.Directory.CreateDirectory(Root);
        Damage(Path("session.bin"));

        _ = await Assert.ThrowsAsync<TamperDetectedException>(async () => await store.LoadAsync(Ct));
    }

    [Fact]
    public async Task The_draft_allowlist_round_trips_and_a_damaged_file_means_an_empty_draft()
    {
        var draft = new DraftAllowlistStore(_store, Path("draft.bin"));
        Assert.Empty((await draft.LoadAsync(Ct)).Apps);
        var saved = new AllowlistDto([new AllowlistEntryDto("Editor", @"C:\Apps\editor.exe", null, null)], ["example.com"]);

        await draft.SaveAsync(saved, Ct);
        AllowlistDto loaded = await draft.LoadAsync(Ct);
        Damage(Path("draft.bin"));

        Assert.Equal(saved.Apps, loaded.Apps);
        Assert.Equal(saved.Domains, loaded.Domains);
        Assert.Empty((await draft.LoadAsync(Ct)).Apps);
    }

    [Fact]
    public async Task Settings_merge_into_the_defaults_and_unknown_or_invalid_values_are_rejected()
    {
        var settings = new ServiceSettingsStore(_store, Path("settings.bin"));
        Assert.Equal("high", (await settings.GetAsync(Ct))[ServiceSettingKeys.EffectsQuality]);

        IReadOnlyList<string> ok = await settings.SetAsync(new Dictionary<string, string> { [ServiceSettingKeys.EffectsQuality] = "off" }, Ct);
        IReadOnlyList<string> unknown = await settings.SetAsync(new Dictionary<string, string> { ["other"] = "x" }, Ct);
        IReadOnlyList<string> invalid = await settings.SetAsync(new Dictionary<string, string> { [ServiceSettingKeys.EffectsQuality] = "max" }, Ct);

        Assert.Empty(ok);
        Assert.Single(unknown);
        Assert.Single(invalid);
        Assert.Equal("off", (await settings.GetAsync(Ct))[ServiceSettingKeys.EffectsQuality]);
        Damage(Path("settings.bin"));
        Assert.Equal("high", (await settings.GetAsync(Ct))[ServiceSettingKeys.EffectsQuality]);
    }

    [Fact]
    public async Task The_app_scan_cache_keeps_everything_but_the_icons()
    {
        var cache = new AppScanCache(_store, Path("apps.bin"));
        Assert.Empty(await cache.LoadAsync(Ct));
        InstalledApp[] apps =
        [
            new("Editor", @"C:\Apps\editor.exe", "ab", null, [1, 2, 3], AppSource.StartMenu | AppSource.Running),
            new("Store app", null, null, "Pkg_123", null, AppSource.StorePackage),
        ];

        await cache.SaveAsync(apps, Ct);
        IReadOnlyList<InstalledApp> loaded = await cache.LoadAsync(Ct);

        Assert.Equal(apps.Select(a => a with { IconPng = null }), loaded);
        Damage(Path("apps.bin"));
        Assert.Empty(await cache.LoadAsync(Ct));
    }

    [Theory]
    [InlineData("Game", null)]
    [InlineData("", @"C:\x.exe")]
    [InlineData("  ", @"C:\x.exe")]
    public void Allowlist_entries_need_a_name_and_an_identifier(string name, string? path)
    {
        IReadOnlyList<string> errors = AllowlistMapping.Validate([new AllowlistEntryDto(name, path, null, null)], []);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Allowlist_validation_accepts_valid_entries_and_rejects_bad_domains_hashes_and_sizes()
    {
        string hash = new('a', 64);

        Assert.Empty(AllowlistMapping.Validate(
            [new AllowlistEntryDto("A", @"C:\a.exe", null, null), new AllowlistEntryDto("B", null, hash, null), new AllowlistEntryDto("C", null, null, "Pkg_1")],
            ["example.com", "*.example.org"]));
        Assert.Single(AllowlistMapping.Validate([new AllowlistEntryDto("B", null, "xyz", null)], []));
        Assert.Single(AllowlistMapping.Validate([], ["https://example.com/path"]));
        Assert.Single(AllowlistMapping.Validate([.. Enumerable.Repeat(new AllowlistEntryDto("A", @"C:\a.exe", null, null), AllowlistMapping.MaxItems + 1)], []));
    }

    [Fact]
    public void Paths_use_the_names_the_watchdog_expects_and_protect_the_install_and_data_directories()
    {
        BlockerPaths paths = BlockerPaths.Create(MockUnixSupport.Path(@"C:\ProgramData"), MockUnixSupport.Path(@"C:\Program Files\Blocker\"));

        Assert.Equal(_fs.Path.Combine(MockUnixSupport.Path(@"C:\ProgramData"), "WindowsDetox"), paths.DataRoot);
        Assert.Equal("hmac.key", System.IO.Path.GetFileName(paths.HmacKeyPath));
        Assert.Equal("supervision.status", System.IO.Path.GetFileName(paths.SupervisionStatusPath));
        Assert.Equal(paths.DataRoot, System.IO.Path.GetDirectoryName(paths.HmacKeyPath));
        Assert.Equal(paths.DataRoot, System.IO.Path.GetDirectoryName(paths.SupervisionStatusPath));
        Assert.Equal("Blocker.Service.exe", System.IO.Path.GetFileName(paths.ServiceExecutablePath));
        Assert.Equal("Blocker.PluginHost.exe", System.IO.Path.GetFileName(paths.PluginHostExecutablePath));
        Assert.Equal([paths.InstallDirectory, paths.DataRoot], paths.ProtectedDirectories);
        Assert.False(paths.InstallDirectory.EndsWith('\\') || paths.InstallDirectory.EndsWith('/'));
        Assert.DoesNotContain(paths.PluginDataDirectory, paths.ProtectedDirectories);
        Assert.False(paths.PluginDataDirectory.StartsWith(paths.DataRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal("WindowsDetoxService", BlockerServiceNames.Service);
        Assert.Equal("WindowsDetoxWatchdog", BlockerServiceNames.Watchdog);
    }
}
