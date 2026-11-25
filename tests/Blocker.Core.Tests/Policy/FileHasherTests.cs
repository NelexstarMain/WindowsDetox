using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Policy;

namespace Blocker.Core.Tests.Policy;

public sealed class FileHasherTests
{
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static readonly string Path = MockUnixSupport.Path(@"C:\Apps\tool.exe");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly MockFileSystem _fs = new();

    private void Write(string path, string content, DateTime written)
    {
        _fs.AddFile(path, new MockFileData(content) { LastWriteTime = written });
    }

    [Fact]
    public async Task Computes_lower_case_hex_sha256()
    {
        Write(Path, "abc", T0);

        Assert.Equal(AbcHash, await new FileHasher(_fs).GetSha256Async(Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unchanged_file_is_served_from_the_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var hasher = new FileHasher(_fs);
        Write(Path, "abc", T0);
        await hasher.GetSha256Async(Path, ct);

        // Same length and timestamp but different bytes: only a cache hit can still return the old hash.
        Write(Path, "abd", T0);

        Assert.Equal(AbcHash, await hasher.GetSha256Async(Path, ct));
        Assert.Equal(1, hasher.CacheCount);
    }

    [Fact]
    public async Task Changed_write_time_invalidates_the_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var hasher = new FileHasher(_fs);
        Write(Path, "abc", T0);
        await hasher.GetSha256Async(Path, ct);

        Write(Path, "abd", T0.AddSeconds(1));

        string? hash = await hasher.GetSha256Async(Path, ct);
        Assert.NotEqual(AbcHash, hash);
        Assert.Equal(64, hash!.Length);
        Assert.Equal(1, hasher.CacheCount);
    }

    [Fact]
    public async Task Changed_length_invalidates_the_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var hasher = new FileHasher(_fs);
        Write(Path, "ab", T0);
        string? first = await hasher.GetSha256Async(Path, ct);

        Write(Path, "abc", T0);

        Assert.NotEqual(first, await hasher.GetSha256Async(Path, ct));
        Assert.Equal(AbcHash, await hasher.GetSha256Async(Path, ct));
    }

    [Fact]
    public async Task Missing_file_yields_null()
    {
        Assert.Null(await new FileHasher(_fs).GetSha256Async(MockUnixSupport.Path(@"C:\nope.exe"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_is_bounded_and_evicts_the_oldest_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var hasher = new FileHasher(_fs, maxCacheEntries: 2);
        string[] paths = [MockUnixSupport.Path(@"C:\a.exe"), MockUnixSupport.Path(@"C:\b.exe"), MockUnixSupport.Path(@"C:\c.exe")];
        foreach (string path in paths)
        {
            Write(path, "abc", T0);
            await hasher.GetSha256Async(path, ct);
        }

        Assert.Equal(2, hasher.CacheCount);

        // The first entry was evicted, so changed content under the same timestamp is re-read; the others are still cached.
        Write(paths[0], "abd", T0);
        Write(paths[2], "abd", T0);
        Assert.NotEqual(AbcHash, await hasher.GetSha256Async(paths[0], ct));
        Assert.Equal(AbcHash, await hasher.GetSha256Async(paths[2], ct));
    }

    [Fact]
    public async Task A_file_over_the_size_cap_yields_null_without_being_read_or_cached()
    {
        var ct = TestContext.Current.CancellationToken;
        var hasher = new FileHasher(_fs);
        Write(Path, "abc", T0);

        Assert.Null(await hasher.GetSha256Async(Path, maxBytes: 2, ct));
        Assert.Equal(0, hasher.CacheCount);
        Assert.Equal(AbcHash, await hasher.GetSha256Async(Path, maxBytes: 3, ct)); // the cap is inclusive
        Assert.Equal(1, hasher.CacheCount);
    }

    [Fact]
    public void Rejects_non_positive_cache_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileHasher(_fs, 0));
    }
}
