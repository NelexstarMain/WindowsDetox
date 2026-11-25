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
    [InlineData(@"C:\Windows\SysWOW64\svchost.exe", CriticalKind.System)]
    [InlineData(@"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", CriticalKind.System)]
    [InlineData(@"C:\Windows\WinSxS\amd64_microsoft-windows-console_31bf3856ad364e35_10.0.19041.1\conhost.exe", CriticalKind.System)]
    [InlineData(@"C:\Windows\System32\consent.exe", CriticalKind.System)] // the UAC prompt
    [InlineData(@"C:\Windows\System32\CredentialUIBroker.exe", CriticalKind.System)]
    [InlineData(@"C:\Program Files\Blocker\Blocker.Service.exe", CriticalKind.BlockerOwn)]
    [InlineData(@"C:\Program Files\Blocker\Blocker.PluginHost.exe", CriticalKind.BlockerOwn)]
    [InlineData(@"C:\Users\me\Downloads\consent.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\Tasks\CredentialUIBroker.exe", CriticalKind.None)]
    [InlineData(@"C:\Users\me\Downloads\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows.old\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\..\Temp\lsass.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\SystemSettings.exe", CriticalKind.None)]
    [InlineData(@"C:\Temp\MsMpEng.exe", CriticalKind.None)]

    // Subtrees of the Windows directory that standard users can write to and run from: a copy named like a critical process is not critical.
    [InlineData(@"C:\Windows\Tasks\conhost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\Tasks\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\Temp\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\tracing\dllhost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\Registration\CRMLog\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\debug\WIA\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\spool\drivers\color\x\dllhost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\spool\drivers\color\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\Tasks\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\sub\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\SysWOW64\Tasks\conhost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\svchost.exe", CriticalKind.None)] // only explorer lives in the Windows directory itself
    [InlineData(@"C:\Windows\System32\explorer.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\SystemApps\SearchHost.exe", CriticalKind.None)] // must be one package level below
    [InlineData(@"C:\Windows\SystemApps\pkg\sub\SearchHost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\SearchHost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\WinSxS\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\WinSxS\a\b\svchost.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\System32\..\Tasks\svchost.exe", CriticalKind.None)]
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

    // Windows hands the console of a starting console program to OpenConsole.exe when Windows Terminal is the default terminal; killing it
    // while the program is still starting fails that program with STATUS_DLL_INIT_FAILED, so it is critical like conhost, in its package only.
    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.22.3232.0_x64__8wekyb3d8bbwe\OpenConsole.exe", CriticalKind.System)]
    [InlineData(@"C:\PROGRAM FILES\WindowsApps\Microsoft.WindowsTerminalPreview_1.23.1.0_x64__8wekyb3d8bbwe\x86\OpenConsole.exe", CriticalKind.System)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.22.3232.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", CriticalKind.None)] // the terminal stays a bypass tool
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.22.3232.0_x64__8wekyb3d8bbwe\notes.exe", CriticalKind.None)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.0_x64__abcdefghijklm\OpenConsole.exe", CriticalKind.None)] // not Microsoft's publisher id
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminalFake_1.0_x64__8wekyb3d8bbwe\OpenConsole.exe", CriticalKind.None)]
    [InlineData(@"C:\Program Files\WindowsApps\Other.App_1.0_x64__8wekyb3d8bbwe\OpenConsole.exe", CriticalKind.None)]
    [InlineData(@"C:\Program Files\WindowsApps\OpenConsole.exe", CriticalKind.None)]
    [InlineData(@"C:\Users\me\WindowsApps\Microsoft.WindowsTerminal_1.0_x64__8wekyb3d8bbwe\OpenConsole.exe", CriticalKind.None)]
    [InlineData(@"C:\Users\me\Downloads\OpenConsole.exe", CriticalKind.None)]
    [InlineData(@"C:\Windows\Tasks\OpenConsole.exe", CriticalKind.None)]
    public void Classifies_the_console_host_of_windows_terminal_by_its_package(string path, CriticalKind expected) =>
        Assert.Equal(expected, new CriticalProcesses(@"C:\Windows", @"C:\Program Files\Blocker", [], @"C:\Program Files\WindowsApps").Classify(path));

    [Fact]
    public void The_console_host_rule_is_off_while_the_packages_directory_is_unknown() =>
        Assert.Equal(
            CriticalKind.None,
            Critical.Classify(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.22.3232.0_x64__8wekyb3d8bbwe\OpenConsole.exe"));

    [Theory]
    [InlineData("explorer.exe", true)]
    [InlineData("OpenConsole.exe", true)] // never redirected to the block screen: it would stop every console program from starting
    [InlineData("MsMpEng", true)] // Defender is critical only in its own roots, but its name is never redirected
    [InlineData("Blocker.UI.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("", false)]
    public void Known_image_names_are_independent_of_the_location(string name, bool expected) =>
        Assert.Equal(expected, CriticalProcesses.IsKnownImageName(name));

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", true)]
    [InlineData(@"C:\WINDOWS\explorer.exe", true)]
    [InlineData(@"\\?\C:\Windows\System32\drivers\x.exe", true)]
    [InlineData(@"C:\Windows\SystemApps\pkg\app.exe", true)]
    [InlineData(@"C:\Windows\..\Games\game.exe", false)]
    [InlineData(@"C:\Windows.old\notepad.exe", false)]
    [InlineData(@"C:\Windowsx\notepad.exe", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Games\game.exe", false)]
    [InlineData(@"", false)]

    // The subtrees every user can write to and run from can hold a copy of any program: they are not trusted.
    [InlineData(@"C:\Windows\Tasks\game.exe", false)]
    [InlineData(@"C:\Windows\Temp\game.exe", false)]
    [InlineData(@"C:\Windows\System32\spool\drivers\color\game.exe", false)]
    [InlineData(@"C:\Windows\System32\Tasks\game.exe", false)]
    public void The_system_directory_is_trusted_except_for_the_subtrees_users_can_write_to(string path, bool expected) =>
        Assert.Equal(expected, Critical.IsInTrustedSystemDirectory(path));

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
