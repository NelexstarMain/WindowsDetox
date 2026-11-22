using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.ServiceHost;
using Blocker.Core.Tests.Security;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class InstallIdStoreTests
{
    private static readonly string IdPath = MockUnixSupport.Path(@"C:\ProgramData\Blocker\install.id");
    private readonly MockFileSystem _fs = new();

    [Fact]
    public void Valid_id_is_returned_unchanged()
    {
        string id = Guid.NewGuid().ToString("N");
        _fs.AddFile(IdPath, new MockFileData(id));

        Assert.Equal(id, InstallIdStore.LoadOrCreate(_fs, IdPath));
        Assert.False(_fs.File.Exists(IdPath + ".bad"));
    }

    [Fact]
    public void Missing_file_creates_an_id_without_a_bad_copy()
    {
        string id = InstallIdStore.LoadOrCreate(_fs, IdPath);

        Assert.Equal(id, _fs.File.ReadAllText(IdPath));
        Assert.False(_fs.File.Exists(IdPath + ".bad"));
    }

    [Fact]
    public void Damaged_file_is_preserved_as_bad_before_a_new_id_is_written()
    {
        _fs.AddFile(IdPath, new MockFileData("not-a-valid-id"));

        string id = InstallIdStore.LoadOrCreate(_fs, IdPath);

        Assert.Equal(32, id.Length);
        Assert.Equal(id, _fs.File.ReadAllText(IdPath));
        Assert.Equal("not-a-valid-id", _fs.File.ReadAllText(IdPath + ".bad"));
    }
}
