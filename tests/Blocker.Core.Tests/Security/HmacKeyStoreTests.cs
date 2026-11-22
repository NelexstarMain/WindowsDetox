using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using Blocker.Core.Abstractions;
using Blocker.Core.Security;

namespace Blocker.Core.Tests.Security;

public sealed class HmacKeyStoreTests
{
    private static readonly string KeyPath = MockUnixSupport.Path(@"C:\ProgramData\Blocker\hmac.key");
    private readonly MockFileSystem _fs = new();
    private readonly FakeSecretProtector _protector = new();

    [Fact]
    public void Creates_a_32_byte_key_and_stores_it_protected()
    {
        byte[] key = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        Assert.Equal(32, key.Length);
        byte[] stored = _fs.File.ReadAllBytes(KeyPath);
        Assert.NotEqual(key, stored);
        Assert.Equal(key, _protector.Unprotect(stored));
    }

    [Fact]
    public void Key_persists_across_instances()
    {
        byte[] first = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        byte[] second = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Repeated_calls_return_the_same_value_without_rewriting_the_file()
    {
        var store = new HmacKeyStore(_fs, _protector, KeyPath);
        byte[] first = store.GetKey();
        byte[] fileAfterFirst = _fs.File.ReadAllBytes(KeyPath);

        Assert.Equal(first, store.GetKey());
        Assert.Equal(fileAfterFirst, _fs.File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void Returned_array_is_a_copy()
    {
        var store = new HmacKeyStore(_fs, _protector, KeyPath);
        byte[] original = store.GetKey();
        byte[] mutated = store.GetKey();
        Array.Clear(mutated);

        Assert.Equal(original, store.GetKey());
    }

    [Fact]
    public void Different_paths_get_independent_keys()
    {
        byte[] a = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();
        byte[] b = new HmacKeyStore(_fs, _protector, MockUnixSupport.Path(@"C:\other\hmac.key")).GetKey();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Corrupt_key_file_is_replaced_with_a_fresh_persisted_key()
    {
        _fs.AddFile(KeyPath, new MockFileData([1, 2, 3]));

        byte[] key = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        Assert.Equal(32, key.Length);
        Assert.Equal(key, new HmacKeyStore(_fs, _protector, KeyPath).GetKey());
    }

    [Fact]
    public void Wrong_sized_key_file_is_replaced()
    {
        _fs.AddFile(KeyPath, new MockFileData(_protector.Protect(new byte[16])));

        byte[] key = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void Transient_unprotect_failure_keeps_the_same_key_and_the_file()
    {
        byte[] original = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();
        byte[] fileBefore = _fs.File.ReadAllBytes(KeyPath);
        var flaky = new FlakyProtector(_protector, failures: 2);

        byte[] loaded = new HmacKeyStore(_fs, flaky, KeyPath).GetKey();

        Assert.Equal(original, loaded);
        Assert.Equal(fileBefore, _fs.File.ReadAllBytes(KeyPath));
        Assert.Empty(_fs.Directory.GetFiles(_fs.Path.GetDirectoryName(KeyPath)!, "*.bad"));
    }

    [Fact]
    public void Persistent_unprotect_failure_moves_the_old_file_aside_and_creates_a_fresh_key()
    {
        byte[] original = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();
        byte[] fileBefore = _fs.File.ReadAllBytes(KeyPath);
        var broken = new FlakyProtector(_protector, failures: int.MaxValue);

        byte[] fresh = new HmacKeyStore(_fs, broken, KeyPath).GetKey();

        Assert.NotEqual(original, fresh);
        string bad = Assert.Single(_fs.Directory.GetFiles(_fs.Path.GetDirectoryName(KeyPath)!, "hmac.key.*.bad"));
        Assert.Matches(@"hmac\.key\.\d{14}\.bad$", bad);
        Assert.Equal(fileBefore, _fs.File.ReadAllBytes(bad));
        Assert.Equal(fresh, _protector.Unprotect(_fs.File.ReadAllBytes(KeyPath)));
        Assert.Equal(3, broken.Calls);
    }

    [Fact]
    public void Wrong_sized_key_file_is_preserved_as_bad()
    {
        byte[] wrongSized = _protector.Protect(new byte[16]);
        _fs.AddFile(KeyPath, new MockFileData(wrongSized));

        _ = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        string bad = Assert.Single(_fs.Directory.GetFiles(_fs.Path.GetDirectoryName(KeyPath)!, "hmac.key.*.bad"));
        Assert.Equal(wrongSized, _fs.File.ReadAllBytes(bad));
    }

    [Fact]
    public void Io_and_access_failures_propagate_and_never_regenerate()
    {
        byte[] fileBefore = _protector.Protect(new byte[32]);
        _fs.AddFile(KeyPath, new MockFileData(fileBefore));

        Assert.Throws<IOException>(() => new HmacKeyStore(_fs, new ThrowingProtector(new IOException("locked")), KeyPath).GetKey());
        Assert.Throws<UnauthorizedAccessException>(() => new HmacKeyStore(_fs, new ThrowingProtector(new UnauthorizedAccessException()), KeyPath).GetKey());

        Assert.Equal(fileBefore, _fs.File.ReadAllBytes(KeyPath));
        Assert.Empty(_fs.Directory.GetFiles(_fs.Path.GetDirectoryName(KeyPath)!, "*.bad"));
    }

    private sealed class ThrowingProtector(Exception failure) : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => throw failure;

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => throw failure;
    }

    private sealed class FlakyProtector(ISecretProtector inner, int failures) : ISecretProtector
    {
        public int Calls { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => inner.Protect(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) =>
            ++Calls <= failures ? throw new CryptographicException("transient") : inner.Unprotect(ciphertext);
    }

    [Fact]
    public void Creation_leaves_no_temp_files()
    {
        _ = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        Assert.Equal([KeyPath], _fs.Directory.GetFiles(_fs.Path.GetDirectoryName(KeyPath)!));
    }
}
