namespace Blocker.Core.ServiceHost;

/// <summary>Names of the two Windows services; the watchdog is started with <c>--partner</c> pointing at the service.</summary>
public static class BlockerServiceNames
{
    /// <summary>The enforcement service (<c>Blocker.Service.exe</c>).</summary>
    public const string Service = "WindowsDetoxService";

    /// <summary>The watchdog service (<c>Blocker.Watchdog.exe</c>).</summary>
    public const string Watchdog = "WindowsDetoxWatchdog";
}

/// <summary>
/// Every file and directory the service uses. The data root (<c>{ProgramData}\WindowsDetox</c>) holds the sealed state and is protected by
/// deny ACEs during a session; plugin data lives next to it, not inside it, because the restricted plugin host must keep writing there.
/// File names of the HMAC key and the supervision status are shared with <c>Blocker.Watchdog</c>.
/// </summary>
public sealed record BlockerPaths
{
    /// <summary>Gets the data root.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Gets the directory the executables live in.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>Gets the directory of the plugin data, outside <see cref="DataRoot"/>.</summary>
    public required string PluginDataDirectory { get; init; }

    /// <summary>Gets the rollback journal.</summary>
    public string JournalPath => Combine(DataRoot, "journal.bin");

    /// <summary>Gets the running session.</summary>
    public string SessionStorePath => Combine(DataRoot, "session.bin");

    /// <summary>Gets the allowlist and domains of the running session.</summary>
    public string SessionPolicyPath => Combine(DataRoot, "session-policy.bin");

    /// <summary>Gets the allowlist the user is editing (used by the next session).</summary>
    public string DraftAllowlistPath => Combine(DataRoot, "allowlist-draft.bin");

    /// <summary>Gets the saved app sets.</summary>
    public string AppSetsPath => Combine(DataRoot, "app-sets.bin");

    /// <summary>Gets the per-app rules and the time zone they are read in.</summary>
    public string AppRulesPath => Combine(DataRoot, "app-rules.bin");

    /// <summary>Gets the foreground time counted per app.</summary>
    public string AppUsagePath => Combine(DataRoot, "app-usage.bin");

    /// <summary>Gets the settings.</summary>
    public string SettingsPath => Combine(DataRoot, "settings.bin");

    /// <summary>Gets the cached application scan used to plan IFEO redirects.</summary>
    public string AppScanCachePath => Combine(DataRoot, "apps-scan.bin");

    /// <summary>Gets the DPAPI-protected HMAC key (also read by the watchdog).</summary>
    public string HmacKeyPath => Combine(DataRoot, "hmac.key");

    /// <summary>Gets the status file the watchdog reads.</summary>
    public string SupervisionStatusPath => Combine(DataRoot, "supervision.status");

    /// <summary>Gets the file that holds the ownership marker value of this installation.</summary>
    public string InstallIdPath => Combine(DataRoot, "install.id");

    /// <summary>Gets the file whose existence allows <c>--dev-mode</c> (DEVMODE builds only).</summary>
    public string DevModeKeyPath => Combine(DataRoot, "devmode.key");

    /// <summary>Gets the directory of installed plugins.</summary>
    public string PluginsDirectory => Combine(DataRoot, "plugins");

    /// <summary>Gets the directory of plugin packages shipped with the application (installed once on first start).</summary>
    public string BundledPluginsDirectory => Combine(InstallDirectory, "bundled-plugins");

    /// <summary>Gets the file listing bundled packages already offered, so a plugin the user removed is not reinstalled.</summary>
    public string BundledPluginsMarkerPath => Combine(DataRoot, "bundled-plugins.txt");

    /// <summary>Gets the log directory.</summary>
    public string LogsDirectory => Combine(DataRoot, "logs");

    /// <summary>Gets the path of <c>Blocker.Service.exe</c>.</summary>
    public string ServiceExecutablePath => Combine(InstallDirectory, "Blocker.Service.exe");

    /// <summary>Gets the path of <c>Blocker.Watchdog.exe</c>.</summary>
    public string WatchdogExecutablePath => Combine(InstallDirectory, "Blocker.Watchdog.exe");

    /// <summary>Gets the path of <c>Blocker.Stub.exe</c>.</summary>
    public string StubExecutablePath => Combine(InstallDirectory, "Blocker.Stub.exe");

    /// <summary>Gets the path of <c>Blocker.PluginHost.exe</c>.</summary>
    public string PluginHostExecutablePath => Combine(InstallDirectory, "Blocker.PluginHost.exe");

    /// <summary>Gets the directories every session creates deny ACEs on.</summary>
    public IReadOnlyList<string> ProtectedDirectories => [InstallDirectory, DataRoot];

    /// <summary>Resolves the paths of the running installation: <c>%ProgramData%\WindowsDetox</c> and the application base directory.</summary>
    /// <returns>The paths.</returns>
    public static BlockerPaths ForCurrentInstallation() =>
        Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppContext.BaseDirectory);

    /// <summary>Builds the paths below <paramref name="programData"/> for an installation in <paramref name="installDirectory"/>.</summary>
    /// <param name="programData">The <c>ProgramData</c> directory.</param>
    /// <param name="installDirectory">The install directory.</param>
    /// <returns>The paths.</returns>
    public static BlockerPaths Create(string programData, string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programData);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        return new BlockerPaths
        {
            DataRoot = Combine(programData, "WindowsDetox"),
            PluginDataDirectory = Combine(programData, "WindowsDetox.PluginData"),
            InstallDirectory = installDirectory.Length > 3 ? installDirectory.TrimEnd('\\', '/') : installDirectory,
        };
    }

    private static string Combine(string directory, string name) => Path.Combine(directory, name);
}
