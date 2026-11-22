using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Security;

namespace Blocker.Core.Tests.Security;

public sealed class AtomicFileTests
{
    private static readonly string Target = MockUnixSupport.Path(@"C:\ProgramData\Blocker\state.bin");
    private readonly MockFileSystem _fs = new();

    [Fact]
    public void Write_removes_stale_temp_siblings_of_the_same_file()
    {
        string stale = Seed(Target + ".aaaa.tmp", age: TimeSpan.FromMinutes(5));

        AtomicFile.Write(_fs, Target, "data"u8);

        Assert.False(_fs.File.Exists(stale));
        Assert.Equal("data"u8.ToArray(), _fs.File.ReadAllBytes(Target));
    }

    [Fact]
    public async Task WriteAsync_removes_stale_temp_siblings_of_the_same_file()
    {
        string stale = Seed(Target + ".aaaa.tmp", age: TimeSpan.FromMinutes(5));

        await AtomicFile.WriteAsync(_fs, Target, "data"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.False(_fs.File.Exists(stale));
    }

    [Fact]
    public void Write_keeps_recent_temp_files_and_files_of_other_targets()
    {
        string recent = Seed(Target + ".bbbb.tmp", age: TimeSpan.FromSeconds(10));
        string otherTemp = Seed(MockUnixSupport.Path(@"C:\ProgramData\Blocker\other.bin.cccc.tmp"), age: TimeSpan.FromHours(1));
        string other = Seed(MockUnixSupport.Path(@"C:\ProgramData\Blocker\state.bin.bak"), age: TimeSpan.FromHours(1));

        AtomicFile.Write(_fs, Target, "data"u8);

        Assert.True(_fs.File.Exists(recent));
        Assert.True(_fs.File.Exists(otherTemp));
        Assert.True(_fs.File.Exists(other));
    }

    private string Seed(string path, TimeSpan age)
    {
        _fs.AddFile(path, new MockFileData([1]) { LastWriteTime = DateTime.UtcNow - age });
        return path;
    }
}
