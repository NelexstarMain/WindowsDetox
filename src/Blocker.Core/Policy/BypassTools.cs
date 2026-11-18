using System.Collections.Frozen;

namespace Blocker.Core.Policy;

/// <summary>
/// Tools that can undo a session (task managers, shells, registry/service editors, debuggers). They are blocked during a session even
/// as children of allowed apps. Matching is by image name and by SHA-256, so a renamed copy of a System32 tool is still caught when its
/// hash was supplied at session start.
/// </summary>
/// <remarks>
/// <c>rundll32</c> and <c>msiexec</c> are included deliberately although Windows itself starts them occasionally (control panel
/// applets, shell verbs, installers); during a session such launches are blocked too. <c>conhost</c> is not here: it is critical.
/// </remarks>
public sealed class BypassTools
{
    private static readonly FrozenSet<string> Names = new[]
    {
        "taskmgr", "resmon", "perfmon", "regedit", "regedt32", "mmc", "procexp", "procexp64", "procexp64a", "processhacker",
        "systeminformer", "cmd", "powershell", "pwsh", "powershell_ise", "wt", "WindowsTerminal", "wscript", "cscript", "mshta",
        "sc", "reg", "net", "net1", "netsh", "wmic", "schtasks", "taskkill", "bcdedit", "msconfig", "gpedit", "secpol", "compmgmt",
        "services", "eventvwr", "control", "rundll32", "msiexec", "certutil", "bitsadmin", "diskpart", "takeown", "icacls", "cacls",
        "runas", "psexec", "psexec64", "autoruns", "autoruns64", "tcpview", "ollydbg", "x64dbg", "windbg", "cheatengine",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenSet<string> _hashes;

    /// <summary>Creates the matcher with hashes of the real binaries (hex SHA-256, any case).</summary>
    public BypassTools(IEnumerable<string> extraHashes)
    {
        ArgumentNullException.ThrowIfNull(extraHashes);
        _hashes = extraHashes.Where(static h => !string.IsNullOrWhiteSpace(h)).Select(static h => h.Trim()).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates the matcher with names only.</summary>
    public BypassTools()
        : this([])
    {
    }

    /// <summary>True when <paramref name="imageName"/> (file name or path) or <paramref name="sha256"/> identifies a bypass tool.</summary>
    public bool IsBypassTool(string imageName, string? sha256) =>
        Names.Contains(WinPath.ImageName(imageName)) || (sha256 is not null && _hashes.Contains(sha256));
}
