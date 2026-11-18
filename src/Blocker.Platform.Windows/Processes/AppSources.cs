using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Blocker.Core.Ports;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Blocker.Platform.Windows.Processes;

/// <summary>One discovery result before merging.</summary>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="ExecutablePath">Executable, when known.</param>
/// <param name="PackageFamilyName">Store package family name, when packaged.</param>
/// <param name="Source">Discovery source.</param>
/// <param name="LogoPng">Icon bytes already provided by the source (Store logos).</param>
internal sealed record AppCandidate(string DisplayName, string? ExecutablePath, string? PackageFamilyName, AppSource Source, byte[]? LogoPng = null);

/// <summary>The four discovery sources of <see cref="AppInventoryScanner"/>.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static class AppSources
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProfileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const int MaxLogoBytes = 256 * 1024;

    /// <summary>All .lnk files of the common and every profile's Start Menu, resolved through IShellLinkW (runs on a dedicated STA thread).</summary>
    public static Task<IReadOnlyList<AppCandidate>> StartMenuAsync(CancellationToken cancellationToken) =>
        RunOnStaThread(() => StartMenu(cancellationToken));

    private static IReadOnlyList<AppCandidate> StartMenu(CancellationToken cancellationToken)
    {
        var result = new List<AppCandidate>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in StartMenuRoots().Where(Directory.Exists))
        {
            foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (!name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                    && ResolveShortcut(shortcut) is { } target
                    && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(target))
                {
                    result.Add(new AppCandidate(name, target, null, AppSource.StartMenu));
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        using var profiles = Registry.LocalMachine.OpenSubKey(ProfileListPath);
        foreach (var sid in profiles?.GetSubKeyNames() ?? [])
        {
            using var profile = profiles!.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is string home)
            {
                yield return Path.Combine(Environment.ExpandEnvironmentVariables(home), @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs");
            }
        }
    }

    private static unsafe string? ResolveShortcut(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, STGM.STGM_READ);
            Span<char> buffer = stackalloc char[1024];
            var data = default(WIN32_FIND_DATAW);
            link.GetPath(buffer, ref data, 0).ThrowOnFailure();
            var end = buffer.IndexOf('\0');
            var target = new string(end < 0 ? buffer : buffer[..end]);
            return target.Length == 0 ? null : target;
        }
        catch (COMException)
        {
            return null; // corrupt or non-file shortcut: not an application candidate
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Uninstall keys of HKLM (64 and 32 bit) and of every loaded user hive.</summary>
    public static IReadOnlyList<AppCandidate> UninstallKeys()
    {
        var result = new List<AppCandidate>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            ReadUninstall(hklm.OpenSubKey(UninstallPath), result);
        }

        foreach (var sid in Registry.Users.GetSubKeyNames().Where(static s => !s.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
        {
            ReadUninstall(Registry.Users.OpenSubKey($@"{sid}\{UninstallPath}"), result);
            ReadUninstall(Registry.Users.OpenSubKey($@"{sid}\{UninstallPath32}"), result);
        }

        return result;
    }

    private static void ReadUninstall(RegistryKey? uninstall, List<AppCandidate> result)
    {
        using var root = uninstall;
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var key = root!.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is not string display || display.Length == 0
                || key.GetValue("SystemComponent") is 1 || key.GetValue("ParentKeyName") is not null)
            {
                continue;
            }

            var exe = FromDisplayIcon(key.GetValue("DisplayIcon") as string) ?? GuessFromFolder(key.GetValue("InstallLocation") as string, display);
            if (exe is not null)
            {
                result.Add(new AppCandidate(display, exe, null, AppSource.Registry));
            }
        }
    }

    /// <summary>Executable named by a DisplayIcon value such as <c>"C:\x\app.exe",0</c>, or null.</summary>
    internal static string? FromDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            text = close < 0 ? text[1..] : text[1..close];
        }
        else if (text.LastIndexOf(',') is var comma and > 0 && int.TryParse(text[(comma + 1)..], out _))
        {
            text = text[..comma];
        }

        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(text) ? text : null;
    }

    private static string? GuessFromFolder(string? folder, string displayName)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { IgnoreInaccessible = true })
            .Where(static f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(f).StartsWith("setup", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(f).StartsWith("update", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var squashed = new string(displayName.Where(char.IsLetterOrDigit).ToArray());
        return candidates.FirstOrDefault(f => squashed.Contains(Path.GetFileNameWithoutExtension(f), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Packaged apps of all users (requires SYSTEM or Administrator); an empty list when the package manager refuses.</summary>
    public static async Task<IReadOnlyList<AppCandidate>> StorePackagesAsync(CancellationToken cancellationToken)
    {
        var result = new List<AppCandidate>();
        IEnumerable<Package> packages;
        try
        {
            packages = new PackageManager().FindPackagesForUser(string.Empty).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
        {
            return result; // not elevated enough: the other sources still work
        }

        foreach (var package in packages.Where(static p => !p.IsFramework && !p.IsResourcePackage && !p.IsBundle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = ManifestExecutable(package);
            foreach (var entry in package.GetAppListEntries())
            {
                var logo = await ReadLogoAsync(entry).ConfigureAwait(false);
                result.Add(new AppCandidate(entry.DisplayInfo.DisplayName, executable, package.Id.FamilyName, AppSource.StorePackage, logo));
            }
        }

        return result;
    }

    private static string? ManifestExecutable(Package package)
    {
        var manifest = Path.Combine(package.InstalledPath, "AppxManifest.xml");
        if (!File.Exists(manifest))
        {
            return null;
        }

        var executable = XDocument.Load(manifest).Descendants()
            .Where(static e => e.Name.LocalName == "Application")
            .Select(static e => (string?)e.Attribute("Executable"))
            .FirstOrDefault(static e => !string.IsNullOrEmpty(e));
        var path = executable is null ? null : Path.Combine(package.InstalledPath, executable);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static async Task<byte[]?> ReadLogoAsync(AppListEntry entry)
    {
        using var stream = await entry.DisplayInfo.GetLogo(new Size(32, 32)).OpenReadAsync();
        if (stream.Size is 0 or > MaxLogoBytes)
        {
            return null;
        }

        using var managed = stream.AsStreamForRead();
        using var buffer = new MemoryStream((int)stream.Size);
        await managed.CopyToAsync(buffer).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        return bytes.AsSpan().StartsWith(PngEncoder.Signature) ? bytes : null; // only PNG is passed through
    }

    /// <summary>Interactive processes (session 0 and the Windows folder are ignored).</summary>
    public static IReadOnlyList<AppCandidate> Running(IEnumerable<ProcessInfo> processes)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        var result = new List<AppCandidate>();
        foreach (var process in processes)
        {
            if (process.SessionId != 0 && process.ImagePath.Length > 0
                && !process.ImagePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && File.Exists(process.ImagePath))
            {
                result.Add(new AppCandidate(DescribeFile(process.ImagePath), process.ImagePath, null, AppSource.Running));
            }
        }

        return result;
    }

    private static string DescribeFile(string path)
    {
        string? description = null;
        try
        {
            description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch (FileNotFoundException)
        {
            // The process image vanished between snapshot and lookup; the file name is a good enough label.
            return Path.GetFileNameWithoutExtension(path);
        }

        return string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim();
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Blocker app inventory (STA)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
