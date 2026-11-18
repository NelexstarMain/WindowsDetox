using Blocker.Platform.Windows.Planning;

namespace Blocker.Platform.Windows.Tests.Planning;

public sealed class BrowserLocatorTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";
    private const string UserLocal = @"C:\Users\ada\AppData\Local";

    private sealed class FakeAppPaths(Dictionary<string, string[]> registered) : IAppPathsRegistry
    {
        public IReadOnlyList<string> GetRegisteredPaths(string executableName) =>
            registered.TryGetValue(executableName, out string[]? paths) ? paths : [];
    }

    private static BrowserLocator Locator(
        string[] existing,
        Dictionary<string, string[]>? registered = null,
        Dictionary<string, string[]>? directories = null,
        params string[] roots)
    {
        HashSet<string> files = new(existing, StringComparer.OrdinalIgnoreCase);
        return new BrowserLocator(
            new FakeAppPaths(registered ?? []),
            files.Contains,
            directory => directories?.GetValueOrDefault(directory) ?? [],
            roots.Length > 0 ? roots : [ProgramFiles, ProgramFilesX86, UserLocal, UserLocal + @"\Programs"]);
    }

    [Fact]
    public void Browsers_in_the_usual_locations_are_found_in_browser_order()
    {
        BrowserLocator locator = Locator(
        [
            ProgramFilesX86 + @"\Microsoft\Edge\Application\msedge.exe",
            ProgramFiles + @"\Mozilla Firefox\firefox.exe",
            ProgramFiles + @"\Google\Chrome\Application\chrome.exe",
            UserLocal + @"\BraveSoftware\Brave-Browser\Application\brave.exe",
            UserLocal + @"\Vivaldi\Application\vivaldi.exe",
            UserLocal + @"\Programs\Opera\opera.exe",
        ]);

        Assert.Equal(
        [
            ProgramFiles + @"\Google\Chrome\Application\chrome.exe",
            ProgramFilesX86 + @"\Microsoft\Edge\Application\msedge.exe",
            UserLocal + @"\BraveSoftware\Brave-Browser\Application\brave.exe",
            ProgramFiles + @"\Mozilla Firefox\firefox.exe",
            UserLocal + @"\Programs\Opera\opera.exe",
            UserLocal + @"\Vivaldi\Application\vivaldi.exe",
        ], locator.Locate());
    }

    [Fact]
    public void Registered_app_paths_are_found_even_in_unusual_places_and_quotes_and_arguments_are_stripped()
    {
        BrowserLocator locator = Locator(
            [@"D:\Portable\Chrome\chrome.exe", @"E:\Apps\Firefox\firefox.exe", @"E:\Apps\Edge\msedge.exe"],
            new Dictionary<string, string[]>
            {
                ["chrome.exe"] = [@"D:\Portable\Chrome\chrome.exe"],
                ["firefox.exe"] = ["\"E:\\Apps\\Firefox\\firefox.exe\" -new-tab"],
                ["msedge.exe"] = [@"E:\Apps\Edge\msedge.exe --profile x"],
            });

        Assert.Equal([@"D:\Portable\Chrome\chrome.exe", @"E:\Apps\Edge\msedge.exe", @"E:\Apps\Firefox\firefox.exe"], locator.Locate());
    }

    [Fact]
    public void Missing_files_wrong_names_and_duplicates_are_dropped()
    {
        string chrome = ProgramFiles + @"\Google\Chrome\Application\chrome.exe";
        BrowserLocator locator = Locator(
            [chrome, @"C:\Evil\notepad.exe"],
            new Dictionary<string, string[]>
            {
                ["chrome.exe"] = [chrome, chrome.ToUpperInvariant(), @"C:\Gone\chrome.exe", @"C:\Evil\notepad.exe"],
                ["firefox.exe"] = [@"C:\Evil\notepad.exe"],
            });

        Assert.Equal([chrome], locator.Locate());
    }

    [Fact]
    public void Operas_versioned_folders_are_searched()
    {
        string versioned = UserLocal + @"\Programs\Opera\118.0.1\opera.exe";
        BrowserLocator locator = Locator(
            [versioned],
            directories: new Dictionary<string, string[]> { [UserLocal + @"\Programs\Opera"] = [UserLocal + @"\Programs\Opera\118.0.1"] });

        Assert.Equal([versioned], locator.Locate());
    }

    [Fact]
    public void Nothing_installed_means_an_empty_list() => Assert.Empty(Locator([]).Locate());

    [Theory]
    [InlineData(@"C:\a\b.exe", @"C:\a\b.exe")]
    [InlineData("  \"C:\\a b\\c.exe\"  ", @"C:\a b\c.exe")]
    [InlineData("\"C:\\a\\b.exe\" --x \"y\"", @"C:\a\b.exe")]
    [InlineData(@"C:\a\b.exe --flag", @"C:\a\b.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("no executable here", null)]
    [InlineData("", null)]
    public void Registered_values_are_reduced_to_the_bare_path(string raw, string? expected) => Assert.Equal(expected, BrowserLocator.CleanRegisteredPath(raw));

    [Fact]
    public void The_browser_list_matches_the_names_the_coordinator_knows()
    {
        Assert.Equal(
            Blocker.Core.Orchestration.EnforcementSettings.KnownBrowserImageNames.Select(static n => n + ".exe").Order(StringComparer.OrdinalIgnoreCase),
            BrowserLocator.BrowserExecutables.Order(StringComparer.OrdinalIgnoreCase));
    }
}
