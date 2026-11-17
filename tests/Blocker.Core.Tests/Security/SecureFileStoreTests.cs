using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Serialization;
using Blocker.Core.Security;

namespace Blocker.Core.Tests.Security;

public sealed record StoreSample(string Name, int Count);

[JsonSerializable(typeof(StoreSample))]
internal sealed partial class StoreSampleContext : JsonSerializerContext;

public sealed class SecureFileStoreTests
{
    private static readonly string Dir = MockUnixSupport.Path(@"C:\ProgramData\Blocker");
    private static readonly string FilePath = MockUnixSupport.Path(@"C:\ProgramData\Blocker\state.bin");
    private readonly MockFileSystem _mock = new();
    private readonly FaultyFileSystem _fs;
    private readonly FakeSecretProtector _protector = new();
    private readonly SecureFileStore _store;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public SecureFileStoreTests()
    {
        _fs = new FaultyFileSystem(_mock);
        _store = new SecureFileStore(_fs, _protector, new HmacKeyStore(_fs, _protector, Path.Combine(Dir, "hmac.key")));
    }

    [Fact]
    public async Task Missing_file_reads_as_null()
    {
        Assert.Null(await _store.ReadAsync(FilePath, _ct));
    }

    [Fact]
    public async Task Write_then_read_round_trips_and_hides_plaintext()
    {
        byte[] payload = "secret journal"u8.ToArray();

        await _store.WriteAsync(FilePath, payload, _ct);

        Assert.Equal(payload, await _store.ReadAsync(FilePath, _ct));
        Assert.Equal(-1, _mock.File.ReadAllBytes(FilePath).AsSpan().IndexOf("secret"u8));
    }

    [Fact]
    public async Task Overwrite_replaces_content_and_leaves_no_temp_files()
    {
        await _store.WriteAsync(FilePath, "one"u8.ToArray(), _ct);
        await _store.WriteAsync(FilePath, "two"u8.ToArray(), _ct);

        Assert.Equal("two"u8.ToArray(), await _store.ReadAsync(FilePath, _ct));
        Assert.Equal([Path.Combine(Dir, "hmac.key"), FilePath], _mock.Directory.GetFiles(Dir).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Crash_while_writing_temp_file_keeps_previous_content()
    {
        await _store.WriteAsync(FilePath, "old"u8.ToArray(), _ct);
        byte[] before = _mock.File.ReadAllBytes(FilePath);

        _fs.FailStreams = true;
        await Assert.ThrowsAsync<SimulatedCrashException>(async () => await _store.WriteAsync(FilePath, "new"u8.ToArray(), _ct));
        _fs.FailStreams = false;

        Assert.Equal(before, _mock.File.ReadAllBytes(FilePath));
        Assert.Equal("old"u8.ToArray(), await _store.ReadAsync(FilePath, _ct));
        Assert.DoesNotContain(_mock.Directory.GetFiles(Dir), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Crash_on_first_write_leaves_no_target_file()
    {
        _fs.FailStreams = true;
        await Assert.ThrowsAsync<SimulatedCrashException>(async () => await _store.WriteAsync(FilePath, "new"u8.ToArray(), _ct));

        Assert.False(_mock.File.Exists(FilePath));
        Assert.Null(await _store.ReadAsync(FilePath, _ct));
    }

    [Fact]
    public async Task Cancelled_write_does_not_touch_the_file()
    {
        await _store.WriteAsync(FilePath, "old"u8.ToArray(), _ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _store.WriteAsync(FilePath, "new"u8.ToArray(), cts.Token));

        Assert.Equal("old"u8.ToArray(), await _store.ReadAsync(FilePath, _ct));
    }

    [Fact]
    public async Task Tampered_file_throws()
    {
        await _store.WriteAsync(FilePath, "payload"u8.ToArray(), _ct);
        byte[] bytes = _mock.File.ReadAllBytes(FilePath);
        bytes[10] ^= 0x01;
        _mock.File.WriteAllBytes(FilePath, bytes);

        await Assert.ThrowsAsync<TamperDetectedException>(async () => await _store.ReadAsync(FilePath, _ct));
    }

    [Fact]
    public async Task Plain_foreign_file_throws()
    {
        _mock.AddFile(FilePath, new MockFileData("not an envelope at all, but long enough to pass length checks........"));

        await Assert.ThrowsAsync<TamperDetectedException>(async () => await _store.ReadAsync(FilePath, _ct));
    }

    [Fact]
    public async Task Json_round_trip_uses_source_generated_metadata()
    {
        var sample = new StoreSample("rule", 3);

        await _store.WriteJsonAsync(FilePath, sample, StoreSampleContext.Default.StoreSample, _ct);

        Assert.Equal(sample, await _store.ReadJsonAsync(FilePath, StoreSampleContext.Default.StoreSample, _ct));
    }

    [Fact]
    public async Task Json_read_of_missing_file_is_null()
    {
        Assert.Null(await _store.ReadJsonAsync(FilePath, StoreSampleContext.Default.StoreSample, _ct));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_tolerates_missing()
    {
        await _store.WriteAsync(FilePath, "x"u8.ToArray(), _ct);

        _store.Delete(FilePath);
        _store.Delete(FilePath);

        Assert.False(_mock.File.Exists(FilePath));
    }
}
