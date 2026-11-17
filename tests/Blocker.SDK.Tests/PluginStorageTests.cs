using System.Text.Json;
using Blocker.Sdk.Storage;

namespace Blocker.SDK.Tests;

public class PluginStorageTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("settings.v1")]
    [InlineData("my_key-2")]
    [InlineData("_hidden")]
    [InlineData("-dash")]
    [InlineData("0")]
    public void IsValid_ValidKey_ReturnsTrue(string key) => Assert.True(PluginStorageKey.IsValid(key));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(".hidden")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("slash/key")]
    [InlineData("ключ")]
    public void IsValid_InvalidKey_ReturnsFalse(string? key) => Assert.False(PluginStorageKey.IsValid(key));

    [Fact]
    public void IsValid_Length128_ReturnsTrue() => Assert.True(PluginStorageKey.IsValid(new string('a', 128)));

    [Fact]
    public void IsValid_Length129_ReturnsFalse() => Assert.False(PluginStorageKey.IsValid(new string('a', 129)));

    [Fact]
    public void ThrowIfInvalid_InvalidKey_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => PluginStorageKey.ThrowIfInvalid(".bad"));

        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void ThrowIfInvalid_ValidKey_DoesNotThrow() => PluginStorageKey.ThrowIfInvalid("good.key");

    [Fact]
    public async Task WriteJsonAsync_ThenReadJsonAsync_RoundTripsValue()
    {
        var storage = new DictionaryStorage();

        await storage.WriteJsonAsync("state", new State("focus", 3), TestContext.Current.CancellationToken);
        State? read = await storage.ReadJsonAsync<State>("state", TestContext.Current.CancellationToken);

        Assert.Equal(new State("focus", 3), read);
        Assert.Equal("""{"name":"focus","count":3}""", storage.Data["state"]);
    }

    [Fact]
    public async Task ReadJsonAsync_MissingKey_ReturnsNull()
    {
        var storage = new DictionaryStorage();

        State? read = await storage.ReadJsonAsync<State>("missing", TestContext.Current.CancellationToken);

        Assert.Null(read);
    }

    [Fact]
    public async Task ReadJsonAsync_InvalidJson_ThrowsJsonException()
    {
        var storage = new DictionaryStorage();
        await storage.WriteAsync("state", "not json", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<JsonException>(
            async () => await storage.ReadJsonAsync<State>("state", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadJsonAsync_NullStorage_ThrowsArgumentNullException() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await PluginStorageExtensions.ReadJsonAsync<State>(null!, "k", TestContext.Current.CancellationToken));

    private sealed record State(string Name, int Count);

    private sealed class DictionaryStorage : IPluginStorage
    {
        public Dictionary<string, string> Data { get; } = [];

        public long QuotaBytes => long.MaxValue;

        public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Data.GetValueOrDefault(key));

        public ValueTask WriteAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Data[key] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Data.Remove(key));

        public ValueTask<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([.. Data.Keys.Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
    }
}
