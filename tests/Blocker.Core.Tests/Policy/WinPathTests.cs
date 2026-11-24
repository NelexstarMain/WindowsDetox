using Blocker.Core.Policy;

namespace Blocker.Core.Tests.Policy;

public sealed class WinPathTests
{
    [Theory]
    [InlineData(@"C:\Games\game.exe")]
    [InlineData("c:/games/game.exe")]
    [InlineData(@"\\?\C:\Games\game.exe")]
    [InlineData(@"Z:\x.exe")]
    public void A_drive_letter_path_is_local(string path)
    {
        Assert.True(WinPath.IsLocalDrivePath(path));
    }

    [Theory]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData("//server/share/game.exe")]
    [InlineData(@"\\?\UNC\server\share\game.exe")]
    [InlineData(@"\\.\pipe\game")]
    [InlineData(@"\\.\C:\game.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\game.exe")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\game.exe")]
    [InlineData(@"\??\C:\game.exe")]
    [InlineData(@"game.exe")]
    [InlineData(@"C:game.exe")]
    [InlineData("")]
    [InlineData("  ")]
    public void Anything_else_is_not(string path)
    {
        Assert.False(WinPath.IsLocalDrivePath(path));
    }
}
