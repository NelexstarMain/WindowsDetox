using Blocker.Core.Ports;
using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

public sealed class AppInventoryMergeTests
{
    [Fact]
    public void Merge_DedupesByExecutablePathIgnoringCase_AndOrsSources()
    {
        var merged = AppInventoryScanner.Merge(
        [
            new AppCandidate("Firefox", @"C:\Program Files\Mozilla\firefox.exe", null, AppSource.StartMenu),
            new AppCandidate("Mozilla Firefox (x64 en-US)", @"c:\program files\mozilla\FIREFOX.EXE", null, AppSource.Registry),
            new AppCandidate("firefox.exe", @"C:\Program Files\Mozilla\firefox.exe", null, AppSource.Running),
        ]);

        var app = Assert.Single(merged);
        Assert.Equal("Firefox", app.DisplayName);
        Assert.Equal(AppSource.StartMenu | AppSource.Registry | AppSource.Running, app.Source);
    }

    [Fact]
    public void Merge_StoreAppsWithoutExecutableAreKeyedByPackageFamilyAndName()
    {
        byte[] logo = [1, 2, 3];

        var merged = AppInventoryScanner.Merge(
        [
            new AppCandidate("Calculator", null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe", AppSource.StorePackage, logo),
            new AppCandidate("Calculator", null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe", AppSource.StorePackage),
            new AppCandidate("Other entry", null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe", AppSource.StorePackage),
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(logo, merged.Single(static a => a.DisplayName == "Calculator").LogoPng);
    }

    [Fact]
    public void Merge_AddsPackageFamilyNameFromLaterSourceAndDropsUnidentifiableEntries()
    {
        var merged = AppInventoryScanner.Merge(
        [
            new AppCandidate("App", @"C:\x\app.exe", null, AppSource.Running),
            new AppCandidate("App", @"C:\x\app.exe", "Pkg_123", AppSource.StorePackage),
            new AppCandidate("Nothing to allowlist", null, null, AppSource.Registry),
        ]);

        var app = Assert.Single(merged);
        Assert.Equal("Pkg_123", app.PackageFamilyName);
        Assert.Equal(AppSource.Running | AppSource.StorePackage, app.Source);
    }
}
