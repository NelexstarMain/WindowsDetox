using System.IO.Abstractions;
using System.Security.Cryptography;
using Blocker.Contracts;
using Blocker.Core.Abstractions;
using Blocker.Core.Clock;
using Blocker.Core.Journal;
using Blocker.Core.Orchestration;
using Blocker.Core.Policy;
using Blocker.Core.ServiceHost;
using Blocker.Platform.Windows.Changes;
using Blocker.Platform.Windows.Ipc;
using Blocker.Platform.Windows.Planning;
using Blocker.Platform.Windows.Processes;
using Blocker.Platform.Windows.Supervision;
using Blocker.Plugin.Templates;
using Microsoft.Extensions.Logging;

namespace Blocker.Service;

/// <summary>Builds the Win32 implementations of every port of <see cref="BlockerServiceApp"/>.</summary>
internal static class WindowsServicePorts
{
    private static readonly string[] RestrictedServices = [BlockerServiceNames.Service, BlockerServiceNames.Watchdog];
    private static readonly string[] TimeServers = ["time.windows.com", "time.cloudflare.com", "pool.ntp.org"];
    private static readonly string[] TimeUrls = ["https://www.microsoft.com", "https://www.cloudflare.com"];

    /// <summary>Creates the ports of the running service.</summary>
    /// <param name="paths">File locations.</param>
    /// <param name="devMode">Whether developer abort is enabled.</param>
    /// <param name="loggers">Logger factory.</param>
    /// <returns>The ports.</returns>
    public static BlockerServicePorts Create(BlockerPaths paths, bool devMode, ILoggerFactory loggers)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string[] extraTrustedRoots = [Path.Combine(programFiles, "Windows Defender"), Path.Combine(programData, "Microsoft", "Windows Defender")];
        string windowsApps = Path.Combine(programFiles, "WindowsApps");
        var critical = new CriticalProcesses(windows, paths.InstallDirectory, extraTrustedRoots, windowsApps);
        var clock = new SystemMonotonicClock();
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var udp = new UdpExchange();

        return new BlockerServicePorts
        {
            FileSystem = new FileSystem(),
            SecretProtector = new DpapiSecretProtector(),
            ProcessMonitor = new EtwProcessMonitor(loggers.CreateLogger<EtwProcessMonitor>()),
            ProcessController = new Win32ProcessController(critical.IsCritical),
            UserSessionLauncher = new WtsUserSessionLauncher(),
            SignatureVerifier = new AuthenticodeVerifier(),
            BrowserFirewall = new WfpBrowserFirewall(),
            AppScanner = new AppInventoryScanner(),
            ChangePlanner = new WindowsChangePlanner(CriticalImageNames()),
            CreateChangeHandlers = installId => CreateChangeHandlers(installId, paths),
            ProtectDataRoot = ProtectDataRoot,
            ServicePipeFactory = new SecurePipeListenerFactory(),
            ServiceIdentityResolver = new WindowsCallerIdentityResolver(
                paths.InstallDirectory,
                new Dictionary<string, IpcCallerRole>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Blocker.UI.exe"] = IpcCallerRole.User | IpcCallerRole.Agent,
                    ["Blocker.Stub.exe"] = IpcCallerRole.Stub,
                },
                loggers.CreateLogger<WindowsCallerIdentityResolver>()),
            PluginHost = new PluginHostPorts
            {
                Launcher = new RestrictedProcessLauncher(),
                PipeFactory = new SecurePipeListenerFactory(SecurePipeProfile.PluginHost),
                IdentityResolver = new WindowsCallerIdentityResolver(
                    paths.InstallDirectory,
                    new Dictionary<string, IpcCallerRole>(StringComparer.OrdinalIgnoreCase) { ["Blocker.PluginHost.exe"] = IpcCallerRole.PluginHost },
                    loggers.CreateLogger<WindowsCallerIdentityResolver>(),
                    trustedImagesOnly: true),
            },
            ServiceInstaller = new ScmServiceInstaller(),
            ServiceControl = new ScmServiceControl(),
            ProcessWaiter = new ProcessWaiter(),
            TimeSources =
            [
                .. TimeServers.Select(host => (INetworkTimeSource)new SntpTimeSource(host, udp, clock)),
                .. TimeUrls.Select(url => new HttpsDateTimeSource(new Uri(url), http, clock)),
            ],
            Clock = clock,
            Time = TimeProvider.System,
            BootMarker = BootMarker(),
            Enforcement = new EnforcementSettings
            {
                InstallDirectory = paths.InstallDirectory,
                WindowsDirectory = windows,
                StubExecutablePath = paths.StubExecutablePath,
                ExtraTrustedRoots = extraTrustedRoots,
                WindowsAppsDirectory = windowsApps,
                BrowserExecutables = BrowserLocator.ForCurrentMachine().Locate(),
                BypassToolImageNames = BypassTools.KnownImageNames,
                BypassToolHashes = HashBypassBinaries(windows),
                ProtectedDirectories = paths.ProtectedDirectories,
                ServiceNames = RestrictedServices,
            },
            PluginTemplate = static async ct =>
            {
                using var buffer = new MemoryStream();
                await PluginStarterTemplate.WriteZipAsync(buffer, ct).ConfigureAwait(false);
                return buffer.ToArray();
            },
            DevMode = devMode,
        };
    }

    /// <summary>Gives the data root and the plugins directory their protected DACLs.</summary>
    /// <param name="paths">File locations.</param>
    public static void ProtectDataRoot(BlockerPaths paths) => DataRootDirectory.Protect(paths.DataRoot, paths.PluginsDirectory);

    /// <summary>Creates the change handlers of the journal; also used by the offline commands.</summary>
    /// <param name="installId">Ownership marker of this installation.</param>
    /// <param name="paths">File locations.</param>
    /// <returns>The handlers.</returns>
    public static IReadOnlyList<IReversibleChangeHandler> CreateChangeHandlers(string installId, BlockerPaths paths) =>
        CreateChangeHandlers(installId, paths, new ScmServiceSecurity());

    internal static IReadOnlyList<IReversibleChangeHandler> CreateChangeHandlers(string installId, BlockerPaths paths, IServiceSecurity serviceSecurity) =>
    [
        new IfeoChangeHandler(installId, CriticalImageNames()),
        new RegistryPolicyChangeHandler(installId),
        new NtfsDenyWriteChangeHandler(paths.ProtectedDirectories),
        new ServiceDaclChangeHandler(serviceSecurity, RestrictedServices.ToDictionary(static name => name, static _ => ServiceSddl.ScmDefaultDacl)),
    ];

    private static string[] CriticalImageNames() => [.. CriticalProcesses.KnownImageNames.Select(static n => n + ".exe")];

    /// <summary>An identity of the current boot that stays the same for the life of the process: the boot time in 30 second buckets.</summary>
    private static Func<long> BootMarker()
    {
        long marker = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Environment.TickCount64 / 1000)) / 30;
        return () => marker;
    }

    /// <summary>Hashes the real bypass binaries so renamed copies of them are caught as well.</summary>
    private static string[] HashBypassBinaries(string windowsDirectory)
    {
        List<string> hashes = [];
        foreach (string directory in new[] { Path.Combine(windowsDirectory, "System32"), Path.Combine(windowsDirectory, "SysWOW64") })
        {
            foreach (string name in BypassTools.KnownImageNames)
            {
                string path = Path.Combine(directory, name + ".exe");
                try
                {
                    if (File.Exists(path))
                    {
                        hashes.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // a locked or unreadable binary is simply not hash-matched; it is still blocked by name
                }
            }
        }

        return [.. hashes.Distinct(StringComparer.Ordinal)];
    }
}
