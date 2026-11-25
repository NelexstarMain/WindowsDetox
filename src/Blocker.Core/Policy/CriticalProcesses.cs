using System.Collections.Frozen;

namespace Blocker.Core.Policy;

/// <summary>Why a process is protected.</summary>
public enum CriticalKind
{
    /// <summary>Not protected.</summary>
    None,

    /// <summary>Windows process that must never be killed.</summary>
    System,

    /// <summary>One of Blocker's own executables.</summary>
    BlockerOwn,
}

/// <summary>
/// Hardcoded set of image names that are never terminated. A name only counts when the image lives in a canonical directory for that name
/// (System32, SysWOW64, the Windows directory itself for <c>explorer</c>, one package level below <c>SystemApps</c>, one component level below
/// <c>WinSxS</c>), or in an administrator-only extra root (Defender), or in the install directory for Blocker's own executables. Directories are
/// matched exactly, never as a prefix: the Windows directory has subtrees every user can write and execute from (<c>Tasks</c>, <c>Temp</c>,
/// <c>tracing</c>, <c>System32\spool\drivers\color</c>...), so a renamed <c>explorer.exe</c> in Downloads or a copy of <c>svchost.exe</c> in
/// <c>C:\Windows\Tasks</c> is not critical. <c>SystemSettings</c> is deliberately absent: it can be used to bypass a session.
/// </summary>
public sealed class CriticalProcesses
{
    private static readonly FrozenDictionary<string, Locations> SystemNames = new Dictionary<string, Locations>(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = Locations.Servicing,
        ["Registry"] = Locations.Servicing,
        ["smss"] = Locations.Servicing,
        ["csrss"] = Locations.Servicing,
        ["wininit"] = Locations.Servicing,
        ["winlogon"] = Locations.Servicing,
        ["services"] = Locations.Servicing,
        ["lsass"] = Locations.Servicing,
        ["lsaiso"] = Locations.Servicing,
        ["svchost"] = Locations.Servicing,
        ["dwm"] = Locations.Servicing,
        ["explorer"] = Locations.WindowsRoot | Locations.SysWow64 | Locations.WinSxS,
        ["fontdrvhost"] = Locations.Servicing,
        ["sihost"] = Locations.Servicing,
        ["ctfmon"] = Locations.Servicing,
        ["taskhostw"] = Locations.Servicing,
        ["RuntimeBroker"] = Locations.Servicing,
        ["StartMenuExperienceHost"] = Locations.SystemApps,
        ["ShellExperienceHost"] = Locations.SystemApps,
        ["SearchHost"] = Locations.SystemApps,
        ["SearchApp"] = Locations.SystemApps,
        ["TextInputHost"] = Locations.SystemApps | Locations.Servicing,
        ["LockApp"] = Locations.SystemApps,
        ["LogonUI"] = Locations.Servicing,
        ["userinit"] = Locations.Servicing,
        ["conhost"] = Locations.Servicing,
        ["dllhost"] = Locations.Servicing,
        ["audiodg"] = Locations.Servicing,
        ["spoolsv"] = Locations.Servicing,
        ["WUDFHost"] = Locations.Servicing,
        ["SecurityHealthService"] = Locations.Servicing,
        ["SecurityHealthSystray"] = Locations.Servicing,
        ["MsMpEng"] = Locations.None, // Defender runs from the administrator-only extra roots
        ["NisSrv"] = Locations.None,
        ["smartscreen"] = Locations.Servicing,
        ["consent"] = Locations.Servicing, // the UAC prompt: killing it denies every elevation
        ["CredentialUIBroker"] = Locations.Servicing, // hosts the credential dialogs of Windows and of apps
        ["ApplicationFrameHost"] = Locations.Servicing,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> OwnNames = new[]
    {
        "Blocker.Service", "Blocker.Watchdog", "Blocker.UI", "Blocker.Stub", "Blocker.PluginHost",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every protected image name (without extension), Windows and Blocker's own, in ordinal order.</summary>
    public static IReadOnlyList<string> KnownImageNames { get; } = [.. SystemNames.Keys.Concat(OwnNames).Order(StringComparer.OrdinalIgnoreCase)];

    private readonly string _windowsDirectory;
    private readonly string[] _writableSubtrees;
    private readonly string[] _extraRoots;
    private readonly string _installDirectory;

    /// <summary>Creates the classifier.</summary>
    /// <param name="windowsDirectory"><c>%SystemRoot%</c>, for example <c>C:\Windows</c>.</param>
    /// <param name="installDirectory">Directory Blocker is installed in.</param>
    /// <param name="additionalSystemDirectories">
    /// Extra trusted roots, whole subtrees, for system names that legitimately live elsewhere (Defender: <c>%ProgramFiles%\Windows Defender</c>,
    /// <c>%ProgramData%\Microsoft\Windows Defender</c>). Must be writable only by administrators.
    /// </param>
    public CriticalProcesses(string windowsDirectory, string installDirectory, IEnumerable<string>? additionalSystemDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        _windowsDirectory = WinPath.Normalize(windowsDirectory);
        _writableSubtrees = [.. EnforcementPolicy.WritableSubtrees.Select(relative => _windowsDirectory + @"\" + relative)];
        _extraRoots = [.. (additionalSystemDirectories ?? []).Select(WinPath.Normalize)];
        _installDirectory = WinPath.Normalize(installDirectory);
    }

    /// <summary>The canonical directories (below the Windows directory) a system image may run from.</summary>
    [Flags]
    private enum Locations
    {
        None = 0,
        System32 = 1,
        SysWow64 = 2,
        WindowsRoot = 4,
        SystemApps = 8,
        WinSxS = 16,

        /// <summary>The default: the two system directories and the servicing store, all writable by TrustedInstaller only.</summary>
        Servicing = System32 | SysWow64 | WinSxS,
    }

    /// <summary>Classifies a process image (DOS-style path; empty paths are never critical).</summary>
    public CriticalKind Classify(string imagePath)
    {
        string path = WinPath.Normalize(imagePath);
        if (path.Length == 0)
        {
            return CriticalKind.None;
        }

        string name = WinPath.ImageName(path);
        if (SystemNames.TryGetValue(name, out Locations locations))
        {
            return IsCanonicalLocation(path, locations) || _extraRoots.Any(root => WinPath.IsUnder(path, root))
                ? CriticalKind.System
                : CriticalKind.None;
        }

        return OwnNames.Contains(name) && WinPath.IsUnder(path, _installDirectory) ? CriticalKind.BlockerOwn : CriticalKind.None;
    }

    /// <summary>True when the process must never be killed.</summary>
    public bool IsCritical(string imagePath) => Classify(imagePath) != CriticalKind.None;

    /// <summary>
    /// True when the image lives in the Windows directory outside the subtrees every user can write to and run from (<c>Tasks</c>, <c>Temp</c>...),
    /// which can hold a copy of any program and so are not trusted.
    /// </summary>
    /// <param name="imagePath">DOS-style image path; empty paths are never in the system directory.</param>
    /// <returns>Whether the image is a system program by its location.</returns>
    public bool IsInTrustedSystemDirectory(string imagePath)
    {
        string path = WinPath.Normalize(imagePath);
        return WinPath.IsUnder(path, _windowsDirectory) && !_writableSubtrees.Any(subtree => WinPath.IsUnder(path, subtree));
    }

    /// <summary>True when <paramref name="imageName"/> (with or without extension) is one of the protected names, wherever the image lives.</summary>
    public static bool IsKnownImageName(string imageName)
    {
        string name = WinPath.ImageName(imageName);
        return SystemNames.ContainsKey(name) || OwnNames.Contains(name);
    }

    private bool IsCanonicalLocation(string path, Locations locations)
    {
        int separator = path.LastIndexOf('\\');
        if (separator < 0)
        {
            return false;
        }

        string directory = path[..separator];
        return (locations.HasFlag(Locations.System32) && directory == _windowsDirectory + @"\system32")
            || (locations.HasFlag(Locations.SysWow64) && directory == _windowsDirectory + @"\syswow64")
            || (locations.HasFlag(Locations.WindowsRoot) && directory == _windowsDirectory)
            || (locations.HasFlag(Locations.SystemApps) && IsDirectChild(directory, _windowsDirectory + @"\systemapps"))
            || (locations.HasFlag(Locations.WinSxS) && IsDirectChild(directory, _windowsDirectory + @"\winsxs"));
    }

    private static bool IsDirectChild(string directory, string parent) =>
        WinPath.IsUnder(directory, parent) && directory.IndexOf('\\', parent.Length + 1) < 0;
}
