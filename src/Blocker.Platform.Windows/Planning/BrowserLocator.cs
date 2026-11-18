using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Blocker.Platform.Windows.Planning;

/// <summary>Reads the <c>App Paths</c> registration of executables (faked in tests).</summary>
internal interface IAppPathsRegistry
{
    /// <summary>Returns every registered path of <paramref name="executableName"/> (<c>chrome.exe</c>), from all hives and registry views.</summary>
    IReadOnlyList<string> GetRegisteredPaths(string executableName);
}

/// <summary>App Paths in HKLM (64-bit and 32-bit view) and HKCU.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryAppPaths : IAppPathsRegistry
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

    public IReadOnlyList<string> GetRegisteredPaths(string executableName)
    {
        List<string> paths = [];
        foreach ((RegistryHive hive, RegistryView view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(Key + executableName);
            if (key?.GetValue(null) is string path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}

/// <summary>
/// Finds the installed browser executables whose traffic must be forced through the filter proxy: the registered <c>App Paths</c> of
/// <c>chrome.exe</c>, <c>msedge.exe</c>, <c>brave.exe</c>, <c>firefox.exe</c>, <c>opera.exe</c> and <c>vivaldi.exe</c>, plus their usual locations below
/// the install roots (Program Files, Program Files (x86), and each user's <c>AppData\Local</c> for per-user installs). Only files that exist
/// are returned, once each.
/// </summary>
public sealed class BrowserLocator
{
    /// <summary>Gets the browser image names, in the order results are listed.</summary>
    public static IReadOnlyList<string> BrowserExecutables { get; } = ["chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "vivaldi.exe"];

    private static readonly (string Executable, string[] RelativePaths)[] Layouts =
    [
        ("chrome.exe", [@"Google\Chrome\Application\chrome.exe"]),
        ("msedge.exe", [@"Microsoft\Edge\Application\msedge.exe"]),
        ("brave.exe", [@"BraveSoftware\Brave-Browser\Application\brave.exe"]),
        ("firefox.exe", [@"Mozilla Firefox\firefox.exe"]),
        ("opera.exe", [@"Opera\opera.exe", @"Opera GX\opera.exe"]),
        ("vivaldi.exe", [@"Vivaldi\Application\vivaldi.exe"]),
    ];

    private static readonly string[] VersionedDirectories = ["Opera", "Opera GX"];

    private readonly IAppPathsRegistry _registry;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<string>> _subdirectories;
    private readonly IReadOnlyList<string> _installRoots;

    internal BrowserLocator(
        IAppPathsRegistry registry,
        Func<string, bool> fileExists,
        Func<string, IReadOnlyList<string>> subdirectories,
        IReadOnlyList<string> installRoots)
    {
        _registry = registry;
        _fileExists = fileExists;
        _subdirectories = subdirectories;
        _installRoots = installRoots;
    }

    /// <summary>Creates a locator over the registry and file system of this machine.</summary>
    /// <returns>The locator.</returns>
    [SupportedOSPlatform("windows")]
    public static BrowserLocator ForCurrentMachine()
    {
        List<string> roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        string users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
        foreach (string profile in SafeSubdirectories(users))
        {
            string local = profile + @"\AppData\Local";
            roots.Add(local);
            roots.Add(local + @"\Programs");
        }

        return new BrowserLocator(new RegistryAppPaths(), File.Exists, SafeSubdirectories, [.. roots.Where(static r => !string.IsNullOrWhiteSpace(r))]);
    }

    /// <summary>Returns the full paths of the installed browsers.</summary>
    /// <returns>Existing executables, each once, ordered by browser and then by path.</returns>
    public IReadOnlyList<string> Locate()
    {
        List<(int Order, string Path)> found = [];
        for (int i = 0; i < Layouts.Length; i++)
        {
            (string executable, string[] relativePaths) = Layouts[i];
            IEnumerable<string> candidates = _registry.GetRegisteredPaths(executable).Select(CleanRegisteredPath).OfType<string>();
            foreach (string root in _installRoots)
            {
                string trimmed = root.TrimEnd('\\');
                candidates = candidates.Concat(relativePaths.Select(relative => $@"{trimmed}\{relative}"));
                if (executable == "opera.exe")
                {
                    candidates = candidates.Concat(VersionedDirectories.SelectMany(directory => _subdirectories($@"{trimmed}\{directory}").Select(sub => $@"{sub}\opera.exe")));
                }
            }

            found.AddRange(candidates.Where(path => IsExecutable(path, executable) && _fileExists(path)).Select(path => (i, path)));
        }

        return [.. found.DistinctBy(static f => f.Path, StringComparer.OrdinalIgnoreCase).OrderBy(static f => f.Order).ThenBy(static f => f.Path, StringComparer.OrdinalIgnoreCase).Select(static f => f.Path)];
    }

    /// <summary>Registered values may be quoted and carry arguments (<c>"C:\x\a.exe" --flag</c>); returns the bare path or <see langword="null"/>.</summary>
    internal static string? CleanRegisteredPath(string raw)
    {
        string text = raw.Trim();
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? null : text[..(exe + 4)];
    }

    private static bool IsExecutable(string path, string executable) =>
        path.Length > executable.Length
        && path[^(executable.Length + 1)] == '\\'
        && path.EndsWith(executable, StringComparison.OrdinalIgnoreCase)
        && path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static IReadOnlyList<string> SafeSubdirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? [.. Directory.EnumerateDirectories(directory)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
