using System.IO.Abstractions.TestingHelpers;
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
    public void Creation_leaves_no_temp_files()
    {
        _ = new HmacKeyStore(_fs, _protector, KeyPath).GetKey();

        Assert.Equal([KeyPath], _fs.Directory.GetFiles(_fs.Path.GetDirectoryName(KeyPath)!));
    }
}
