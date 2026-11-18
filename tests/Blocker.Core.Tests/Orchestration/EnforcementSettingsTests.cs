using Blocker.Core.Orchestration;
using Blocker.Core.Policy;
using Blocker.Core.Ports;

namespace Blocker.Core.Tests.Orchestration;

public sealed class EnforcementSettingsTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static EnforcementSettings Valid => new()
    {
        InstallDirectory = OrchestrationHarness.InstallDir,
        WindowsDirectory = @"C:\Windows",
        StubExecutablePath = OrchestrationHarness.StubPath,
    };

    [Fact]
    public void Validate_RejectsBlankPathsAndBadPorts()
    {
        Valid.Validate();
        Assert.Throws<ArgumentException>(() => (Valid with { InstallDirectory = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (Valid with { WindowsDirectory = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (Valid with { StubExecutablePath = "" }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Valid with { ProxyPort = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Valid with { ProxyPort = 65536 }).Validate());
        (Valid with { ProxyPort = 0 }).Validate();
        (Valid with { ProxyPort = 65535 }).Validate();
    }

    [Fact]
    public void Defaults_NameTheBlockerServicesAndKnownBrowsers()
    {
        Assert.Equal(["Blocker.Service", "Blocker.Watchdog"], Valid.ServiceNames);
        Assert.Equal([OrchestrationHarness.InstallDir], Valid.ResolveProtectedDirectories());
        Assert.Equal(["brave", "chrome", "firefox", "msedge", "opera", "vivaldi"], EnforcementSettings.KnownBrowserImageNames);
        Assert.Equal([@"D:\Data"], (Valid with { ProtectedDirectories = [@"D:\Data"] }).ResolveProtectedDirectories());
    }

    [Fact]
    public void ResolveBrowsers_KeepsDistinctKnownBrowsersInOrder()
    {
        const string firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";
        EnforcementSettings settings = Valid with
        {
            BrowserExecutables =
            [
                OrchestrationHarness.Chrome,
                OrchestrationHarness.Chrome.ToUpperInvariant(),
                @"C:\Tools\evil.exe",
                "",
                OrchestrationHarness.Edge,
                firefox,
                @"C:\Brave\BRAVE.EXE",
                @"C:\Opera\opera.exe",
                @"C:\Vivaldi\vivaldi.exe",
            ],
        };

        Assert.Equal(
            [OrchestrationHarness.Chrome, OrchestrationHarness.Edge, firefox, @"C:\Brave\BRAVE.EXE", @"C:\Opera\opera.exe", @"C:\Vivaldi\vivaldi.exe"],
            settings.ResolveBrowsers());
    }

    [Fact]
    public void BuildIfeoImageNames_ListsBypassToolsAndUncoveredScannedApps()
    {
        var allowlist = new Allowlist(
        [
            new AllowlistEntry("Editor", @"C:\Apps\editor.exe"),
            new AllowlistEntry("Tool", null, Hash),
        ]);
        var critical = new CriticalProcesses(@"C:\Windows", OrchestrationHarness.InstallDir);
        EnforcementSettings settings = Valid with
        {
            BypassToolImageNames = ["taskmgr", "TASKMGR.exe", "cmd.exe", "services", "explorer", "Blocker.Stub", "  ", "regedit"],
            ScannedApps =
            [
                new InstalledApp("Game", @"C:\Games\game.exe", null, null, null, AppSource.StartMenu),
                new InstalledApp("Editor", @"C:\Apps\editor.exe", null, null, null, AppSource.StartMenu),
                new InstalledApp("Other editor", @"C:\Other\Editor.exe", null, null, null, AppSource.Registry),
                new InstalledApp("Tool", @"C:\Apps\tool.exe", Hash, null, null, AppSource.Registry),
                new InstalledApp("Store app", null, null, "Pkg_123", null, AppSource.StorePackage),
                new InstalledApp("Library", @"C:\x\lib.dll", null, null, null, AppSource.Running),
                new InstalledApp("Own UI", @"C:\Program Files\Blocker\Blocker.UI.exe", null, null, null, AppSource.Running),
                new InstalledApp("Game again", @"D:\Mirror\GAME.EXE", null, null, null, AppSource.Running),
            ],
        };

        // services.exe and explorer.exe are critical; editor.exe and tool.exe are allowlisted (IFEO matches by name only).
        Assert.Equal(["cmd.exe", "game.exe", "regedit.exe", "taskmgr.exe"], settings.BuildIfeoImageNames(allowlist, critical));
    }
}
