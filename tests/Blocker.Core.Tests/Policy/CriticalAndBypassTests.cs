using Blocker.Core.Policy;

namespace Blocker.Core.Tests.Policy;

public sealed class CriticalAndBypassTests
{
    private static readonly CriticalProcesses Critical = new(@"C:\Windows", @"C:\Program Files\Blocker", [@"C:\ProgramData\Microsoft\Windows Defender"]);

    [Theory]
    [InlineData(@"C:\Windows\System32\lsass.exe", CriticalKind.System)]
    [InlineData(@"C:\WINDOWS\system32\SVCHOST.EXE", CriticalKind.System)]
    [InlineData(@"c:/windows/explorer.exe", CriticalKind.System)]
    [InlineData(@"C:\Windows\SystemApps\ShellExperienceHost_x\ShellExperienceHost.exe", CriticalKind.System)]
    [InlineData(@"\\?\C:\Windows\System32\csrss.exe", CriticalKind.System)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18\MsMpEng.exe", CriticalKind.System)]
    [InlineData(@"C:\Program Files\Blocker\Blocker.Service.exe", CriticalKind.BlockerOwn)]
    [InlineData(@"C:\Program Files\Blocker\Blocker.PluginHost.exe", CriticalKind.BlockerOwn)]
    [InlineData(@"C:\Users\me\Downloads\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows.old\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\..\Temp\lsass.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\SystemSettings.exe", CriticalKind.None)]
    [InlineData(@"C:\Temp\MsMpEng.exe", CriticalKind.None)]
    [InlineData(@"C:\Temp\Blocker.Service.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\Blocker.Service.exe", CriticalKind.None)]
    [InlineData(@"C:\Program Files\Blocker\lsass.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\lsass.dll", CriticalKind.None)]
    [InlineData(@"explorer.exe", CriticalKind.None)]
    [InlineData(@"", CriticalKind.None)]
    public void Classifies_by_name_and_location(string path, CriticalKind expected)
    {
        Assert.Equal(expected, Critical.Classify(path));
        Assert.Equal(expected != CriticalKind.None, Critical.IsCritical(path));
    }

    [Fact]
    public void Constructor_rejects_blank_directories()
    {
        Assert.Throws<ArgumentException>(() => new CriticalProcesses(" ", @"C:\x"));
        Assert.Throws<ArgumentException>(() => new CriticalProcesses(@"C:\Windows", ""));
    }

    [Theory]
    [InlineData("taskmgr.exe", true)]
    [InlineData(@"C:\Windows\System32\CMD.EXE", true)]
    [InlineData("powershell", true)]
    [InlineData("WindowsTerminal.exe", true)]
    [InlineData("cheatengine", true)]
    [InlineData("rundll32.exe", true)]
    [InlineData("msiexec.exe", true)]
    [InlineData("cmd.exe.", true)]
    [InlineData("conhost.exe", false)]
    [InlineData("notepad.exe", false)]
    [InlineData("cmdx.exe", false)]
    [InlineData("cmd.txt", false)]
    [InlineData("", false)]
    public void Matches_bypass_tools_by_image_name(string image, bool expected)
    {
        Assert.Equal(expected, new BypassTools().IsBypassTool(image, null));
    }

    [Fact]
    public void Matches_renamed_copies_by_hash_ignoring_case_and_blank_hashes()
    {
        var tools = new BypassTools([new string('a', 64), "  ", new string('B', 64)]);

        Assert.True(tools.IsBypassTool("totally-innocent.exe", new string('A', 64)));
        Assert.True(tools.IsBypassTool("totally-innocent.exe", new string('b', 64)));
        Assert.False(tools.IsBypassTool("totally-innocent.exe", new string('c', 64)));
        Assert.False(tools.IsBypassTool("totally-innocent.exe", null));
    }
}
