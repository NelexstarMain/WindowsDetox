using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Tests.Security;
using Blocker.Sdk;
using Blocker.Sdk.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocker.Core.Tests.PluginRuntime;

public sealed class PluginStorageAndStoreTests
{
    private const string StoragePath = "/data/com.test.p/storage.json";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly PluginId Id = new("com.test.p");

    private readonly MockFileSystem _mock = new();

    private FileBackedPluginStorage Open(long quota = FileBackedPluginStorage.DefaultQuotaBytes) =>
        new(_mock, StoragePath, NullLogger.Instance, quota);

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
    public async Task Invalid_keys_are_rejected(string key)
    {
        using FileBackedPluginStorage storage = Open();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.WriteAsync(key, "v", Ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadAsync(key, Ct).AsTask());
    }

    [Fact]
    public async Task The_quota_counts_keys_and_values_and_a_rejected_write_changes_nothing()
    {
        using FileBackedPluginStorage storage = Open(quota: 40);
        await storage.WriteAsync("k", "123456789", Ct); // (1 + 9) * 2 = 20 bytes
        await storage.WriteAsync("k", "1234567890123456789", Ct); // replacing: (1 + 19) * 2 = 40 bytes, exactly the quota

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

    [Fact]
    public async Task A_damaged_file_starts_empty_and_is_replaced_by_the_next_write()
    {
        _mock.AddFile(StoragePath, new MockFileData("{not json"));
        using FileBackedPluginStorage storage = Open();

        Assert.Empty(await storage.ListKeysAsync(null, Ct));
        await storage.WriteAsync("a", "1", Ct);

        using FileBackedPluginStorage reopened = Open();
        Assert.Equal("1", await reopened.ReadAsync("a", Ct));
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
    public void An_uninitialized_plugin_id_is_refused()
    {
        var store = new PluginStore(_mock, "/plugins", "/data");

        Assert.Throws<ArgumentException>(() => store.PluginDirectory(default));
    }
}
