using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App")]
    [InlineData(@"c:\app\", @"C:\app")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\a\b.exe", @"D:\a\b.exe")]
    [InlineData(@"C:\PROGRA~1\x", @"C:\PROGRA~1\x")]
    public void Valid_paths_are_normalized(string path, string expected)
    {
        Assert.True(WindowsPath.TryNormalize(path, out string? normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:")]
    [InlineData(@"C:app")]
    [InlineData(@"app\x")]
    [InlineData(@"\app")]
    [InlineData("C:/app")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\app")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\a\.\b")]
    [InlineData(@"C:\a\\b")]
    [InlineData(@"C:\a\\")]
    [InlineData(@"C:\a\b.")]
    [InlineData(@"C:\a\b ")]
    [InlineData(@"C:\a\b:ads")]
    [InlineData(@"C:\a\b*")]
    [InlineData(@"C:\a\b?")]
    [InlineData("C:\\a\\b\"")]
    [InlineData("C:\\a\\b\tc")]
    [InlineData(@"C:\a\CON")]
    [InlineData(@"C:\a\nul.txt")]
    [InlineData(@"C:\a\com1.exe")]
    public void Unsafe_paths_are_refused(string? path)
    {
        Assert.False(WindowsPath.TryNormalize(path, out string? normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void Helpers_work_on_normalized_paths()
    {
        Assert.Equal("b.exe", WindowsPath.GetFileName(@"C:\a\b.exe"));
        Assert.True(WindowsPath.AreEqual(@"C:\A", @"c:\a"));
        Assert.Throws<ArgumentException>(() => WindowsPath.Normalize("x", "p"));
    }
}
