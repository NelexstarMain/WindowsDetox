using System.Collections.Frozen;
using Blocker.Core.Policy;
using Blocker.Core.Ports;

namespace Blocker.Core.Orchestration;

/// <summary>Static, per-installation inputs of the <see cref="EnforcementCoordinator"/>. Paths are DOS-style and resolved by the platform.</summary>
public sealed record EnforcementSettings
{
    private static readonly FrozenSet<string> KnownBrowsers = new[] { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the image names (without extension) of the browsers whose traffic is forced through the proxy.</summary>
    public static IReadOnlyList<string> KnownBrowserImageNames { get; } = [.. KnownBrowsers.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Directory Blocker is installed in; its executables are never touched.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>The Windows directory (<c>%SystemRoot%</c>).</summary>
    public required string WindowsDirectory { get; init; }

    /// <summary>Full path of <c>Blocker.Stub.exe</c>: the block screen and the IFEO debugger target.</summary>
    public required string StubExecutablePath { get; init; }

    /// <summary>Additional trusted roots for critical system names (for example the Defender directories).</summary>
    public IReadOnlyList<string> ExtraTrustedRoots { get; init; } = [];

    /// <summary>Resolved browser executables; entries whose image name is not in <see cref="KnownBrowserImageNames"/> are ignored.</summary>
    public IReadOnlyList<string> BrowserExecutables { get; init; } = [];

    /// <summary>Image names of the bypass tools (<c>taskmgr</c>, <c>regedit</c>, ...; the <c>.exe</c> extension is optional).</summary>
    public IReadOnlyList<string> BypassToolImageNames { get; init; } = [];

    /// <summary>Hex SHA-256 of the real bypass binaries, so renamed copies are caught as well.</summary>
    public IReadOnlyList<string> BypassToolHashes { get; init; } = [];

    /// <summary>Installed applications found by the scanner; the ones that are not allowlisted get an IFEO redirect.</summary>
    public IReadOnlyList<InstalledApp> ScannedApps { get; init; } = [];

    /// <summary>Directories that receive deny ACEs; the install directory when empty.</summary>
    public IReadOnlyList<string> ProtectedDirectories { get; init; } = [];

    /// <summary>Services whose DACL is hardened (Blocker service and watchdog).</summary>
    public IReadOnlyList<string> ServiceNames { get; init; } = ["Blocker.Service", "Blocker.Watchdog"];

    /// <summary>Loopback port of the filter proxy; 0 picks an ephemeral one.</summary>
    public int ProxyPort { get; init; }

    /// <summary>Checks the values the coordinator relies on.</summary>
    /// <exception cref="ArgumentException">A required path is blank or the port is out of range.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstallDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(WindowsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(StubExecutablePath);
        ArgumentOutOfRangeException.ThrowIfNegative(ProxyPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ProxyPort, 65535);
    }

    /// <summary>Returns the distinct browser executables that have a known browser image name.</summary>
    public IReadOnlyList<string> ResolveBrowsers() =>
    [
        .. BrowserExecutables
            .Where(static p => !string.IsNullOrWhiteSpace(p) && KnownBrowsers.Contains(WinPath.ImageName(p)))
            .DistinctBy(static p => WinPath.Normalize(p), StringComparer.Ordinal),
    ];

    /// <summary>Returns the directories to protect (<see cref="InstallDirectory"/> when none were configured).</summary>
    public IReadOnlyList<string> ResolveProtectedDirectories() =>
        ProtectedDirectories.Count > 0 ? [.. ProtectedDirectories] : [InstallDirectory];

    /// <summary>
    /// Builds the sorted, distinct IFEO image names (<c>name.exe</c>): bypass tools plus scanned apps the allowlist does not cover.
    /// IFEO matches by name only, so a name is dropped when an allowlisted application has that name, and critical Windows or Blocker
    /// names are never included (<c>services.exe</c> is on the bypass list, but redirecting it would break Windows). Images in the Windows
    /// directory and inbox Windows packages are left to the policy, which checks signatures and paths; an IFEO redirect would show the block
    /// screen every time Windows starts one of its own helpers.
    /// </summary>
    public IReadOnlyList<string> BuildIfeoImageNames(Allowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
        foreach (AllowlistEntry entry in allowlist.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.ExecutablePath))
            {
                allowed.Add(FileName(entry.ExecutablePath));
            }
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string tool in BypassToolImageNames.Where(static n => !string.IsNullOrWhiteSpace(n)))
        {
            names.Add(WithExe(FileName(tool.Trim())));
        }

        string windowsDirectory = WinPath.Normalize(WindowsDirectory);
        foreach (InstalledApp app in ScannedApps)
        {
            if (string.IsNullOrWhiteSpace(app.ExecutablePath) || !app.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || SystemPackages.IsSystemPackage(app.PackageFamilyName)
                || WinPath.IsUnder(WinPath.Normalize(app.ExecutablePath), windowsDirectory))
            {
                continue;
            }

            string name = FileName(app.ExecutablePath);
            if (allowlist.Match(app.ExecutablePath, app.Sha256, app.PackageFamilyName) is null)
            {
                names.Add(name);
            }
            else
            {
                allowed.Add(name);
            }
        }

        return
        [
            .. names
                .Where(n => !allowed.Contains(n) && !CriticalProcesses.IsKnownImageName(n))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string FileName(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    private static string WithExe(string name) => name.Contains('.', StringComparison.Ordinal) ? name : name + ".exe";
}
