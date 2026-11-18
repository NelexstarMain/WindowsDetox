using System.IO.Abstractions;
using System.Security.Principal;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Journal;
using Blocker.Core.Ports;
using Blocker.Core.Security;
using Blocker.Core.ServiceHost;
using Blocker.Platform.Windows.Changes;
using Blocker.Platform.Windows.Processes;
using Microsoft.Extensions.Logging;

namespace Blocker.Service;

/// <summary>Process exit codes of <c>Blocker.Service.exe</c>.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int BadArguments = 2;
    public const int SessionActive = 3;
    public const int Incomplete = 4;
    public const int NotAdministrator = 5;
}

/// <summary>The collaborators of the one-shot commands; tests substitute fakes.</summary>
/// <param name="Installer">Installs, starts and removes the services.</param>
/// <param name="PluginData">Creates and removes the plugin data directory.</param>
/// <param name="Rollback">Rolls everything back while idle and returns an exit code.</param>
/// <param name="DevAbort">Asks the running service to abort its session (developer mode only) and returns an exit code.</param>
/// <param name="IsAdministrator">Whether the caller is an elevated administrator.</param>
internal sealed record ServiceCommandPorts(
    IServiceInstaller Installer,
    IPluginDataDirectory PluginData,
    Func<CancellationToken, Task<int>> Rollback,
    Func<CancellationToken, Task<int>> DevAbort,
    Func<bool> IsAdministrator);

/// <summary>The one-shot commands <c>--install</c>, <c>--uninstall</c>, <c>--recover</c> and <c>--dev-abort</c>. All of them need an elevated administrator.</summary>
internal static class ServiceCommands
{
    /// <summary>Runs a one-shot command against the real system.</summary>
    /// <param name="command">The command.</param>
    /// <param name="paths">File locations.</param>
    /// <param name="loggers">Logger factory (console and file).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The process exit code.</returns>
    /// <param name="devMode">Whether <c>--dev-mode</c> was given; <c>--install</c> then registers the service with it.</param>
    public static Task<int> RunAsync(ServiceCommandKind command, BlockerPaths paths, bool devMode, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync(
            command,
            paths,
            devMode,
            new ServiceCommandPorts(
                new ScmServiceInstaller(),
                new PluginDataDirectory(new FileSystem()),
                ct => RollbackAsync(paths, loggers, ct),
                ct => DevAbortAsync(loggers, ct),
                IsElevatedAdministrator),
            cancellationToken);

    /// <summary>Runs a one-shot command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="paths">File locations.</param>
    /// <param name="devMode">Whether <c>--dev-mode</c> was given; <c>--install</c> then registers the service with it.</param>
    /// <param name="ports">Collaborators.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(ServiceCommandKind command, BlockerPaths paths, bool devMode, ServiceCommandPorts ports, CancellationToken cancellationToken)
    {
        if (!ports.IsAdministrator())
        {
            await Console.Error.WriteLineAsync("This command needs an elevated administrator.").ConfigureAwait(false);
            return ExitCodes.NotAdministrator;
        }

        try
        {
            return command switch
            {
                ServiceCommandKind.Install => Install(paths, devMode, ports),
                ServiceCommandKind.DevAbort => await ports.DevAbort(cancellationToken).ConfigureAwait(false),
                ServiceCommandKind.Uninstall => await UninstallAsync(paths, ports, cancellationToken).ConfigureAwait(false),
                ServiceCommandKind.Recover => await RecoverAsync(ports, cancellationToken).ConfigureAwait(false),
                _ => ExitCodes.BadArguments,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"{command} failed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failed;
        }
    }

    private static async Task<int> DevAbortAsync(ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        await using var client = new IpcClient(IpcPipeNames.Service, loggers.CreateLogger<IpcClient>());
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        DevAbortResponse response = await client.InvokeAsync<DevAbortResponse>(IpcMethods.SessionDevAbort, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(response.Aborted
            ? "The session was ended and every change was rolled back."
            : "Nothing was aborted: no session is running, or the service is not in developer mode.");
        return response.Aborted ? ExitCodes.Success : ExitCodes.Failed;
    }

    private static int Install(BlockerPaths paths, bool devMode, ServiceCommandPorts ports)
    {
        // The directory exists before the services start, so the plugin host never races its creation.
        ports.PluginData.Provision(paths.PluginDataDirectory);
        IServiceInstaller installer = ports.Installer;

        // Developer mode is still honoured only by DEVMODE builds with the dev key file present (see DevModeGate).
        installer.InstallOrUpdate(BlockerServiceNames.Service, "WindowsDetox Blocker", paths.ServiceExecutablePath, devMode ? "--dev-mode" : string.Empty);
        installer.InstallOrUpdate(
            BlockerServiceNames.Watchdog,
            "WindowsDetox Blocker Watchdog",
            paths.WatchdogExecutablePath,
            $"--partner {BlockerServiceNames.Service}");
        installer.EnsureStarted(BlockerServiceNames.Service);
        installer.EnsureStarted(BlockerServiceNames.Watchdog);
        Console.WriteLine("Both services are installed and running.");
        return ExitCodes.Success;
    }

    /// <summary>Rolls everything back while no session runs (the services keep protecting a running one), then removes the watchdog, the service and the plugin data directory.</summary>
    private static async Task<int> UninstallAsync(BlockerPaths paths, ServiceCommandPorts ports, CancellationToken cancellationToken)
    {
        int code = await ports.Rollback(cancellationToken).ConfigureAwait(false);
        if (code != ExitCodes.Success)
        {
            await Console.Error.WriteLineAsync("The services were not removed.").ConfigureAwait(false);
            return code;
        }

        // The watchdog goes first: while it runs it could restart the service it supervises.
        ports.Installer.Uninstall(BlockerServiceNames.Watchdog);
        ports.Installer.Uninstall(BlockerServiceNames.Service);
        ports.PluginData.Remove(paths.PluginDataDirectory);
        Console.WriteLine("Both services were removed.");
        return ExitCodes.Success;
    }

    /// <summary>Offline rollback: the break-glass alternative for installations whose service cannot run, as long as no session is persisted.</summary>
    private static async Task<int> RecoverAsync(ServiceCommandPorts ports, CancellationToken cancellationToken)
    {
        int code = await ports.Rollback(cancellationToken).ConfigureAwait(false);
        if (code == ExitCodes.Success)
        {
            Console.WriteLine("Recovery finished. Start the service again with: net start " + BlockerServiceNames.Service);
        }

        return code;
    }

    private static async Task<int> RollbackAsync(BlockerPaths paths, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var fileSystem = new FileSystem();
        var protector = new DpapiSecretProtector();
        var secure = new SecureFileStore(fileSystem, protector, new HmacKeyStore(fileSystem, protector, paths.HmacKeyPath));
        string installId = InstallIdStore.LoadOrCreate(fileSystem, paths.InstallIdPath);
        var journal = new RollbackJournal(secure, paths.JournalPath, WindowsServicePorts.CreateChangeHandlers(installId, paths), installId, loggers.CreateLogger<RollbackJournal>());
        MaintenanceResult result = await ServiceMaintenance.RollbackWhenIdleAsync(new SecureSessionStore(secure, paths.SessionStorePath), journal, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.Message);
        return result.Outcome switch
        {
            MaintenanceOutcome.Done => ExitCodes.Success,
            MaintenanceOutcome.SessionActive => ExitCodes.SessionActive,
            _ => ExitCodes.Incomplete,
        };
    }

    private static bool IsElevatedAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
