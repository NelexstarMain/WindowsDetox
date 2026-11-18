using System.IO.Abstractions;
using System.Security.Principal;
using Blocker.Core.Journal;
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

/// <summary>The one-shot commands <c>--install</c>, <c>--uninstall</c> and <c>--recover</c>. All of them need an elevated administrator.</summary>
internal static class ServiceCommands
{
    /// <summary>Runs a one-shot command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="paths">File locations.</param>
    /// <param name="loggers">Logger factory (console and file).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(ServiceCommandKind command, BlockerPaths paths, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!IsElevatedAdministrator())
        {
            await Console.Error.WriteLineAsync("This command needs an elevated administrator.").ConfigureAwait(false);
            return ExitCodes.NotAdministrator;
        }

        try
        {
            return command switch
            {
                ServiceCommandKind.Install => Install(paths),
                ServiceCommandKind.Uninstall => await UninstallAsync(paths, loggers, cancellationToken).ConfigureAwait(false),
                ServiceCommandKind.Recover => await RecoverAsync(paths, loggers, cancellationToken).ConfigureAwait(false),
                _ => ExitCodes.BadArguments,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"{command} failed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failed;
        }
    }

    private static int Install(BlockerPaths paths)
    {
        var installer = new ScmServiceInstaller();
        installer.InstallOrUpdate(BlockerServiceNames.Service, "WindowsDetox Blocker", paths.ServiceExecutablePath, string.Empty);
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

    /// <summary>Rolls everything back while no session runs (the services keep protecting a running one), then removes the watchdog and the service.</summary>
    private static async Task<int> UninstallAsync(BlockerPaths paths, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        int code = await RollbackAsync(paths, loggers, cancellationToken).ConfigureAwait(false);
        if (code != ExitCodes.Success)
        {
            await Console.Error.WriteLineAsync("The services were not removed.").ConfigureAwait(false);
            return code;
        }

        // The watchdog goes first: while it runs it could restart the service it supervises.
        var installer = new ScmServiceInstaller();
        installer.Uninstall(BlockerServiceNames.Watchdog);
        installer.Uninstall(BlockerServiceNames.Service);
        Console.WriteLine("Both services were removed.");
        return ExitCodes.Success;
    }

    /// <summary>Offline rollback: the break-glass alternative for installations whose service cannot run, as long as no session is persisted.</summary>
    private static async Task<int> RecoverAsync(BlockerPaths paths, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        int code = await RollbackAsync(paths, loggers, cancellationToken).ConfigureAwait(false);
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
