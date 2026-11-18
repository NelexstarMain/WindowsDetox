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
/// Hardcoded set of image names that are never terminated. A name only counts when the image lives in the expected location
/// (Windows directory, or the install directory for Blocker's own executables), so a renamed <c>explorer.exe</c> in Downloads is not critical.
/// <c>SystemSettings</c> is deliberately absent: it can be used to bypass a session.
/// </summary>
public sealed class CriticalProcesses
{
    private static readonly FrozenSet<string> SystemNames = new[]
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "explorer",
        "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "LockApp", "LogonUI", "userinit", "conhost", "dllhost", "audiodg", "spoolsv",
        "WUDFHost", "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "smartscreen", "ApplicationFrameHost",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> OwnNames = new[]
    {
        "Blocker.Service", "Blocker.Watchdog", "Blocker.UI", "Blocker.Stub", "Blocker.PluginHost",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every protected image name (without extension), Windows and Blocker's own, in ordinal order.</summary>
    public static IReadOnlyList<string> KnownImageNames { get; } = [.. SystemNames.Concat(OwnNames).Order(StringComparer.OrdinalIgnoreCase)];

    private readonly string[] _systemRoots;
    private readonly string _installDirectory;

    /// <summary>Creates the classifier.</summary>
    /// <param name="windowsDirectory"><c>%SystemRoot%</c>, for example <c>C:\Windows</c>.</param>
    /// <param name="installDirectory">Directory Blocker is installed in.</param>
    /// <param name="additionalSystemDirectories">
    /// Extra trusted roots for system names that legitimately live elsewhere (Defender: <c>%ProgramFiles%\Windows Defender</c>,
    /// <c>%ProgramData%\Microsoft\Windows Defender</c>). Writable only by administrators.
    /// </param>
    public CriticalProcesses(string windowsDirectory, string installDirectory, IEnumerable<string>? additionalSystemDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        var roots = new List<string> { WinPath.Normalize(windowsDirectory) };
        foreach (string dir in additionalSystemDirectories ?? [])
        {
            roots.Add(WinPath.Normalize(dir));
        }

        _systemRoots = [.. roots];
        _installDirectory = WinPath.Normalize(installDirectory);
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
        if (SystemNames.Contains(name))
        {
            foreach (string root in _systemRoots)
            {
                if (WinPath.IsUnder(path, root))
                {
                    return CriticalKind.System;
                }
            }
        }
        else if (OwnNames.Contains(name) && WinPath.IsUnder(path, _installDirectory))
        {
            return CriticalKind.BlockerOwn;
        }

        return CriticalKind.None;
    }

    /// <summary>True when the process must never be killed.</summary>
    public bool IsCritical(string imagePath) => Classify(imagePath) != CriticalKind.None;
}
