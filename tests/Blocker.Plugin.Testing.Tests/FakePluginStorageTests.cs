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
    public async Task Quota_CountsUtf8BytesOfKeyAndValue()
    {
        var storage = new InMemoryPluginStorage();
        Assert.Equal(1_048_576, storage.QuotaBytes);

        await storage.WriteAsync("k", new string('x', 1_048_575), TestContext.Current.CancellationToken);

        Assert.Equal(1_048_576, storage.UsedBytes);
        var ex = await Assert.ThrowsAsync<PluginStorageException>(async () => await storage.WriteAsync("j", "", TestContext.Current.CancellationToken));
        Assert.Equal("Storage quota of 1048576 bytes exceeded (1048577 bytes requested).", ex.Message);
        Assert.Null(await storage.ReadAsync("j", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Quota_OneByteOverLimitIsRejected()
    {
        var storage = new InMemoryPluginStorage();

        await Assert.ThrowsAsync<PluginStorageException>(async () => await storage.WriteAsync("k", new string('x', 1_048_576), TestContext.Current.CancellationToken));

        Assert.Equal(0, storage.UsedBytes);
    }

    [Theory]
    [InlineData("a&b", 1 + 8)] // '&' is written as \u0026 (6 bytes)
    [InlineData("zażółć", 1 + 26)] // 'z' and 'a' stay; the four non-ASCII chars are written as \uXXXX (4 * 6 bytes)
    [InlineData("😀", 1 + 12)] // a surrogate pair is two \uXXXX escapes
    public async Task Quota_CountsTheJsonEscapedUtf8SizeThatIsWritten(string value, int expected)
    {
        var storage = new InMemoryPluginStorage();

        await storage.WriteAsync("k", value, TestContext.Current.CancellationToken);

        Assert.Equal(expected, storage.UsedBytes);
    }

    // Attribute arguments cannot carry unpaired surrogates (metadata is UTF-8), so they come from a member.
    public static TheoryData<string> LoneSurrogates => ["\ud800", "a\udc00b", "\ud800x"];

    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public async Task LoneSurrogateValues_AreRejectedAndNothingIsStored(string value)
    {
        var storage = new InMemoryPluginStorage();

        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.WriteAsync("k", value, TestContext.Current.CancellationToken));

        Assert.Null(await storage.ReadAsync("k", TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.UsedBytes);
    }

    [Fact]
    public async Task Quota_ReplaceAndDeleteFreeSpace_RejectedWriteKeepsOldValue()
    {
        var storage = new InMemoryPluginStorage(quotaBytes: 10);
        await storage.WriteAsync("k", "abc", TestContext.Current.CancellationToken);
        Assert.Equal(4, storage.UsedBytes);

        await Assert.ThrowsAsync<PluginStorageException>(async () => await storage.WriteAsync("j", "abcdefghi", TestContext.Current.CancellationToken));
        await storage.WriteAsync("k", "abcdef", TestContext.Current.CancellationToken);
        Assert.Equal(7, storage.UsedBytes);
        await storage.WriteAsync("k", "a", TestContext.Current.CancellationToken);
        Assert.Equal(2, storage.UsedBytes);
        await storage.WriteAsync("j", "abcdefg", TestContext.Current.CancellationToken);
        Assert.Equal(10, storage.UsedBytes);
        Assert.True(await storage.DeleteAsync("j", TestContext.Current.CancellationToken));

        Assert.Equal(2, storage.UsedBytes);
        Assert.Equal("a", await storage.ReadAsync("k", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData(".hidden")]
    [InlineData("slash/key")]
    [InlineData("abc\n")]
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
