using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Tests.Security;
using Blocker.Sdk;
using Blocker.Sdk.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.PluginRuntime;

public sealed class PluginStorageAndStoreTests
{
    private const string StoragePath = "/data/com.test.p/storage.json";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly PluginId Id = new("com.test.p");

    private readonly MockFileSystem _mock = new();

    private FileBackedPluginStorage Open(long quota = FileBackedPluginStorage.DefaultQuotaBytes, TimeProvider? time = null) =>
        new(_mock, StoragePath, NullLogger.Instance, quota, time);

    private IEnumerable<string> BadFiles() =>
        _mock.Directory.GetFiles("/data/com.test.p", "storage.json.*.bad").Select(_mock.Path.GetFileName).Order(StringComparer.Ordinal).Select(f => f!);

    // ---- storage ----

    [Fact]
    public async Task Values_round_trip_and_survive_a_new_instance()
    {
        using (FileBackedPluginStorage storage = Open())
        {
            await storage.WriteAsync("count", "7", Ct);
            await storage.WriteAsync("name", "x", Ct);
            Assert.Equal("7", await storage.ReadAsync("count", Ct));
        }

        using FileBackedPluginStorage reopened = Open();
        Assert.Equal("7", await reopened.ReadAsync("count", Ct));
        Assert.Null(await reopened.ReadAsync("missing", Ct));
        Assert.Equal(["count", "name"], await reopened.ListKeysAsync(null, Ct));
        Assert.Equal(["name"], await reopened.ListKeysAsync("na", Ct));
        Assert.True(await reopened.DeleteAsync("count", Ct));
        Assert.False(await reopened.DeleteAsync("count", Ct));
        using FileBackedPluginStorage third = Open();
        Assert.Equal(["name"], await third.ListKeysAsync(null, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("abc\n")]
    public async Task Invalid_keys_are_rejected(string key)
    {
        using FileBackedPluginStorage storage = Open();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.WriteAsync(key, "v", Ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadAsync(key, Ct).AsTask());
    }

    [Fact]
    public async Task The_quota_counts_keys_and_values_and_a_rejected_write_changes_nothing()
    {
        using FileBackedPluginStorage storage = Open(quota: 20);
        await storage.WriteAsync("k", "123456789", Ct); // 1 + 9 = 10 bytes
        await storage.WriteAsync("k", "1234567890123456789", Ct); // replacing: 1 + 19 = 20 bytes, exactly the quota

        PluginStorageException ex = await Assert.ThrowsAsync<PluginStorageException>(() => storage.WriteAsync("other", "x", Ct).AsTask());

        Assert.Contains("quota", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await storage.ReadAsync("other", Ct));
        Assert.Equal("1234567890123456789", await storage.ReadAsync("k", Ct));
        Assert.Equal(1_048_576, Open().QuotaBytes);
    }

    [Fact]
    public async Task A_crash_while_writing_keeps_the_old_file_and_the_old_memory_state()
    {
        var faulty = new FaultyFileSystem(_mock);
        using var storage = new FileBackedPluginStorage(faulty, StoragePath, NullLogger.Instance);
        await storage.WriteAsync("a", "1", Ct);
        string before = _mock.File.ReadAllText(StoragePath);

        faulty.FailStreams = true;
        await Assert.ThrowsAsync<PluginStorageException>(() => storage.WriteAsync("a", "2", Ct).AsTask());
        await Assert.ThrowsAsync<PluginStorageException>(() => storage.DeleteAsync("a", Ct).AsTask());
        faulty.FailStreams = false;

        Assert.Equal(before, _mock.File.ReadAllText(StoragePath));
        Assert.Equal("1", await storage.ReadAsync("a", Ct));
        Assert.DoesNotContain(_mock.Directory.GetFiles("/data/com.test.p"), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        await storage.WriteAsync("a", "3", Ct);
        Assert.Equal("3", await storage.ReadAsync("a", Ct));
    }

    [Theory]
    [InlineData("&&", "j")]
    [InlineData("żż", "j")]
    [InlineData("😀", "j")]
    public async Task The_quota_counts_the_json_escaped_utf8_bytes_that_are_written(string value, string other)
    {
        // "&&" is written as \u0026\u0026: 1 + 12 = 13 bytes, so a second entry no longer fits in 20 bytes.
        using FileBackedPluginStorage storage = Open(quota: 20);
        await storage.WriteAsync("k", value, Ct);

        await Assert.ThrowsAsync<PluginStorageException>(() => storage.WriteAsync(other, value, Ct).AsTask());

        Assert.True(_mock.FileInfo.New(StoragePath).Length <= 20 + "{\"k\":\"\"}".Length);
    }

    // Attribute arguments cannot carry unpaired surrogates (metadata is UTF-8), so they come from a member.
    public static TheoryData<string> LoneSurrogates => ["\ud800", "a\udc00b"];

    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public async Task A_value_with_a_lone_surrogate_is_rejected_and_nothing_is_written(string value)
    {
        using FileBackedPluginStorage storage = Open();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.WriteAsync("k", value, Ct).AsTask());

        Assert.Null(await storage.ReadAsync("k", Ct));
        Assert.False(_mock.File.Exists(StoragePath));
    }

    [Fact]
    public async Task A_damaged_file_is_set_aside_and_storage_starts_empty()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 10, 30, 5, TimeSpan.Zero));
        _mock.AddFile(StoragePath, new MockFileData("{not json"));
        using FileBackedPluginStorage storage = Open(time: time);

        Assert.Empty(await storage.ListKeysAsync(null, Ct));
        await storage.WriteAsync("a", "1", Ct);

        Assert.Equal(["storage.json.20261122103005.bad"], BadFiles());
        Assert.Equal("{not json", _mock.File.ReadAllText("/data/com.test.p/storage.json.20261122103005.bad"));
        using FileBackedPluginStorage reopened = Open();
        Assert.Equal("1", await reopened.ReadAsync("a", Ct));
    }

    [Fact]
    public async Task Only_the_three_newest_damaged_copies_are_kept()
    {
        foreach (string stamp in new[] { "20260101000000", "20260102000000", "20260103000000" })
        {
            _mock.AddFile($"{StoragePath}.{stamp}.bad", new MockFileData("old"));
        }

        _mock.AddFile(StoragePath, new MockFileData("[1,"));
        using FileBackedPluginStorage storage = Open(time: new FakeTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));

        _ = await storage.ListKeysAsync(null, Ct);

        Assert.Equal(["storage.json.20260102000000.bad", "storage.json.20260103000000.bad", "storage.json.20260201000000.bad"], BadFiles());
    }

    [Fact]
    public async Task A_read_failure_is_a_storage_exception_without_host_paths_and_keeps_the_inner_exception()
    {
        _mock.AddFile(StoragePath, new MockFileData("{}") { AllowedFileShare = FileShare.None });
        using FileBackedPluginStorage storage = Open();

        PluginStorageException ex = await Assert.ThrowsAsync<PluginStorageException>(() => storage.ReadAsync("a", Ct).AsTask());

        Assert.DoesNotContain("/data", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("storage.json", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Empty(BadFiles());
    }

    [Fact]
    public async Task A_write_failure_keeps_the_inner_exception()
    {
        var faulty = new FaultyFileSystem(_mock);
        using var storage = new FileBackedPluginStorage(faulty, StoragePath, NullLogger.Instance);
        faulty.FailStreams = true;

        PluginStorageException ex = await Assert.ThrowsAsync<PluginStorageException>(() => storage.WriteAsync("a", "1", Ct).AsTask());

        Assert.IsType<SimulatedCrashException>(ex.InnerException);
        Assert.DoesNotContain("/data", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_keys_in_a_tampered_file_are_dropped()
    {
        _mock.AddFile(StoragePath, new MockFileData("{\"good\":\"1\",\"../evil\":\"2\"}"));
        using FileBackedPluginStorage storage = Open();

        Assert.Equal(["good"], await storage.ListKeysAsync(null, Ct));
    }

    // ---- store ----

    [Fact]
    public async Task OpenStorage_returns_one_shared_instance_per_plugin()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");
        FileBackedPluginStorage first = store.OpenStorage(Id, NullLogger.Instance, quotaBytes: 20);
        FileBackedPluginStorage second = store.OpenStorage(Id, NullLogger.Instance, quotaBytes: 20);

        await first.WriteAsync("a", "123456789", Ct); // 10 of 20 bytes
        Assert.Equal("123456789", await second.ReadAsync("a", Ct));
        await second.WriteAsync("b", "12345678", Ct); // 9 more bytes
        PluginStorageException ex = await Assert.ThrowsAsync<PluginStorageException>(() => first.WriteAsync("c", "12", Ct).AsTask());

        Assert.Same(first, second);
        Assert.Contains("quota", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["a", "b"], await first.ListKeysAsync(null, Ct));
        Assert.NotSame(first, store.OpenStorage(new PluginId("com.test.q"), NullLogger.Instance));
    }

    [Fact]
    public async Task Remove_drops_the_cached_storage_so_a_reinstall_starts_empty()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");
        await store.InstallAsync(Id, new byte[] { 1 }, enabled: true, Ct);
        FileBackedPluginStorage before = store.OpenStorage(Id, NullLogger.Instance);
        await before.WriteAsync("a", "1", Ct);

        Assert.True(store.Remove(Id));
        await store.InstallAsync(Id, new byte[] { 1 }, enabled: true, Ct);
        FileBackedPluginStorage after = store.OpenStorage(Id, NullLogger.Instance);

        Assert.NotSame(before, after);
        Assert.Empty(await after.ListKeysAsync(null, Ct));
    }

    [Fact]
    public async Task Installed_plugins_are_listed_with_their_enabled_flag()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");
        await store.InstallAsync(Id, new byte[] { 1, 2, 3 }, enabled: true, Ct);
        await store.InstallAsync(new PluginId("com.test.q"), new byte[] { 4 }, enabled: false, Ct);

        IReadOnlyList<StoredPlugin> listed = await store.ListAsync(Ct);

        Assert.Equal([new StoredPlugin(Id, true), new StoredPlugin(new PluginId("com.test.q"), false)], listed);
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.ReadPackageAsync(Id, Ct));
        Assert.True(await store.SetEnabledAsync(Id, false, Ct));
        Assert.False((await store.ListAsync(Ct)).First(p => p.Id == Id).Enabled);
        Assert.False(await store.SetEnabledAsync(new PluginId("com.test.none"), true, Ct));
        Assert.Null(await store.ReadPackageAsync(new PluginId("com.test.none"), Ct));
    }

    [Fact]
    public async Task Foreign_directories_and_damaged_state_never_enable_a_plugin()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");
        await store.InstallAsync(Id, new byte[] { 1 }, enabled: true, Ct);
        _mock.AddFile("/plugins/com.test.p/state.json", new MockFileData("garbage"));
        _mock.AddFile("/plugins/Not A Plugin/package.zip", new MockFileData("zip"));
        _mock.AddFile("/plugins/..evil/package.zip", new MockFileData("zip"));
        _mock.AddDirectory("/plugins/com.test.empty");

        IReadOnlyList<StoredPlugin> listed = await store.ListAsync(Ct);

        Assert.Equal([new StoredPlugin(Id, false)], listed);
    }

    [Fact]
    public async Task Remove_deletes_the_package_and_the_data()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");
        await store.InstallAsync(Id, new byte[] { 1 }, enabled: true, Ct);
        _mock.AddFile(store.StoragePath(Id), new MockFileData("{}"));

        Assert.True(store.Remove(Id));

        Assert.Empty(await store.ListAsync(Ct));
        Assert.False(_mock.Directory.Exists("/data/com.test.p"));
        Assert.False(store.Remove(Id));
    }

    [Fact]
    public async Task A_failed_remove_leaves_the_plugin_listed_so_it_can_be_retried()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");
        await store.InstallAsync(Id, new byte[] { 1 }, enabled: true, Ct);
        _mock.AddFile(store.StoragePath(Id), new MockFileData("{}") { AllowedFileShare = FileShare.None });
        _mock.File.SetAttributes(store.StoragePath(Id), FileAttributes.ReadOnly);

        _ = Assert.ThrowsAny<Exception>(() => store.Remove(Id));

        Assert.Equal([new StoredPlugin(Id, true)], await store.ListAsync(Ct));
        Assert.True(_mock.File.Exists(store.StoragePath(Id)));
        _mock.File.SetAttributes(store.StoragePath(Id), FileAttributes.Normal);
        Assert.True(store.Remove(Id));
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public void An_uninitialized_plugin_id_is_refused()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");

        Assert.Throws<ArgumentException>(() => store.PluginDirectory(default));
    }
}
