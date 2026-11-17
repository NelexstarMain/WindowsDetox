using Blocker.Sdk.Storage;

namespace Blocker.Plugin.Testing.Tests;

public sealed class FakePluginStorageTests
{
    [Fact]
    public async Task WriteRead_Delete_ListKeys()
    {
        var storage = new InMemoryPluginStorage();

        await storage.WriteAsync("b", "2", TestContext.Current.CancellationToken);
        await storage.WriteAsync("a.one", "1", TestContext.Current.CancellationToken);
        await storage.WriteAsync("a.two", "3", TestContext.Current.CancellationToken);

        Assert.Equal("2", await storage.ReadAsync("b", TestContext.Current.CancellationToken));
        Assert.Null(await storage.ReadAsync("missing", TestContext.Current.CancellationToken));
        Assert.Equal(["a.one", "a.two", "b"], await storage.ListKeysAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["a.one", "a.two"], await storage.ListKeysAsync("a.", TestContext.Current.CancellationToken));
        Assert.True(await storage.DeleteAsync("b", TestContext.Current.CancellationToken));
        Assert.False(await storage.DeleteAsync("b", TestContext.Current.CancellationToken));
        Assert.Equal(["a.one", "a.two"], await storage.ListKeysAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Quota_CountsUtf16BytesOfKeyAndValue()
    {
        var storage = new InMemoryPluginStorage();
        Assert.Equal(1_048_576, storage.QuotaBytes);

        await storage.WriteAsync("k", new string('x', 524_287), TestContext.Current.CancellationToken);

        Assert.Equal(1_048_576, storage.UsedBytes);
        var ex = await Assert.ThrowsAsync<PluginStorageException>(async () => await storage.WriteAsync("j", "", TestContext.Current.CancellationToken));
        Assert.Equal("Storage quota of 1048576 bytes exceeded (1048578 bytes requested).", ex.Message);
        Assert.Null(await storage.ReadAsync("j", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Quota_OneCharacterOverLimitIsRejected()
    {
        var storage = new InMemoryPluginStorage();

        await Assert.ThrowsAsync<PluginStorageException>(async () => await storage.WriteAsync("k", new string('x', 524_288), TestContext.Current.CancellationToken));

        Assert.Equal(0, storage.UsedBytes);
    }

    [Fact]
    public async Task Quota_ReplaceAndDeleteFreeSpace_RejectedWriteKeepsOldValue()
    {
        var storage = new InMemoryPluginStorage(quotaBytes: 10);
        await storage.WriteAsync("k", "ab", TestContext.Current.CancellationToken);
        Assert.Equal(6, storage.UsedBytes);

        await Assert.ThrowsAsync<PluginStorageException>(async () => await storage.WriteAsync("j", "abcd", TestContext.Current.CancellationToken));
        await storage.WriteAsync("k", "abc", TestContext.Current.CancellationToken);
        Assert.Equal(8, storage.UsedBytes);
        await storage.WriteAsync("k", "a", TestContext.Current.CancellationToken);
        Assert.Equal(4, storage.UsedBytes);
        await storage.WriteAsync("j", "ab", TestContext.Current.CancellationToken);
        Assert.Equal(10, storage.UsedBytes);
        Assert.True(await storage.DeleteAsync("j", TestContext.Current.CancellationToken));

        Assert.Equal(4, storage.UsedBytes);
        Assert.Equal("a", await storage.ReadAsync("k", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData(".hidden")]
    [InlineData("slash/key")]
    public async Task InvalidKeys_AreRejectedOnEveryOperation(string key)
    {
        var storage = new InMemoryPluginStorage();

        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.WriteAsync(key, "v", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.ReadAsync(key, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.DeleteAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KeyLengthBoundary()
    {
        var storage = new InMemoryPluginStorage();

        await storage.WriteAsync(new string('a', PluginStorageKey.MaxLength), "v", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.WriteAsync(new string('a', PluginStorageKey.MaxLength + 1), "v", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JsonExtensions_RoundTrip_ThroughContextStorage()
    {
        var context = new FakePluginContext();

        await context.Storage.WriteJsonAsync("state", new Counter(3, "x"), TestContext.Current.CancellationToken);

        Assert.Equal("""{"count":3,"label":"x"}""", await context.FakeStorage.ReadAsync("state", TestContext.Current.CancellationToken));
        Assert.Equal(new Counter(3, "x"), await context.Storage.ReadJsonAsync<Counter>("state", TestContext.Current.CancellationToken));
        Assert.Null(await context.Storage.ReadJsonAsync<Counter>("none", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        var storage = new InMemoryPluginStorage();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.WriteAsync("k", "v", cts.Token));
    }

    private sealed record Counter(int Count, string Label);
}
