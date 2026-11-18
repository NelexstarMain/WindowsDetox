using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Blocker.Core.Journal;
using Blocker.Core.Security;
using Blocker.Core.Session;

namespace Blocker.Core.ServiceHost;

/// <summary>What the service executable was asked to do.</summary>
public enum ServiceCommandKind
{
    /// <summary>Run as the service (or in the console with <c>--console</c>).</summary>
    Run,

    /// <summary>Install and start both services.</summary>
    Install,

    /// <summary>Roll everything back, then remove both services.</summary>
    Uninstall,

    /// <summary>Roll everything back offline; the break-glass alternative for damaged installations.</summary>
    Recover,

    /// <summary>Ask the running service to end the session at once (works only when the service runs in developer mode).</summary>
    DevAbort,
}

/// <summary>Command line of <c>Blocker.Service.exe</c>: <c>[--console] [--dev-mode] | --install [--dev-mode] | --uninstall | --recover | --dev-abort</c>.</summary>
/// <param name="Command">The command.</param>
/// <param name="Console">Run interactively with console logging instead of through the service control manager.</param>
/// <param name="DevMode">Enable developer abort (honoured only by DEVMODE builds with the dev key file present).</param>
public sealed record ServiceArguments(ServiceCommandKind Command, bool Console, bool DevMode)
{
    /// <summary>Parses the process arguments.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="arguments">The parsed arguments on success.</param>
    /// <param name="error">What is wrong on failure.</param>
    /// <returns><see langword="true"/> when the command line is valid.</returns>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out ServiceArguments? arguments, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ServiceCommandKind command = ServiceCommandKind.Run;
        bool console = false;
        bool devMode = false;
        bool commandGiven = false;
        foreach (string arg in args)
        {
            ServiceCommandKind? requested = arg switch
            {
                "--install" => ServiceCommandKind.Install,
                "--uninstall" => ServiceCommandKind.Uninstall,
                "--recover" => ServiceCommandKind.Recover,
                "--dev-abort" => ServiceCommandKind.DevAbort,
                _ => null,
            };
            if (requested is { } value)
            {
                if (commandGiven)
                {
                    (arguments, error) = (null, "Only one of --install, --uninstall, --recover and --dev-abort may be given.");
                    return false;
                }

                (command, commandGiven) = (value, true);
            }
            else if (arg == "--console")
            {
                console = true;
            }
            else if (arg == "--dev-mode")
            {
                devMode = true;
            }
            else
            {
                (arguments, error) = (null, $"Unknown argument '{arg}'.");
                return false;
            }
        }

        arguments = new ServiceArguments(command, console, devMode);
        error = null;
        return true;
    }
}

/// <summary>How an offline rollback ended.</summary>
public enum MaintenanceOutcome
{
    /// <summary>Everything was rolled back.</summary>
    Done,

    /// <summary>A session is running: nothing was touched.</summary>
    SessionActive,

    /// <summary>Some changes could not be reverted.</summary>
    Incomplete,
}

/// <summary>Result of <see cref="ServiceMaintenance.RollbackWhenIdleAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">Short English text for the console.</param>
public sealed record MaintenanceResult(MaintenanceOutcome Outcome, string Message);

/// <summary>Offline operations on the journal for <c>--uninstall</c> and <c>--recover</c>; they never run while a session does.</summary>
public static class ServiceMaintenance
{
    /// <summary>
    /// Refuses while a session is persisted; otherwise replays the journal in reverse and then sweeps every artifact that carries the
    /// installation marker. A session file that cannot be verified is discarded (the service does the same at start-up), because it
    /// cannot be resumed and nothing could ever end it.
    /// </summary>
    /// <param name="sessions">The session store.</param>
    /// <param name="journal">The rollback journal.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    public static async ValueTask<MaintenanceResult> RollbackWhenIdleAsync(ISessionStore sessions, RollbackJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(journal);
        string note = string.Empty;
        try
        {
            if (await sessions.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return new MaintenanceResult(MaintenanceOutcome.SessionActive, "A session is running. End it first (it expires on its own, or use the break-glass code).");
            }
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            await sessions.ClearAsync(cancellationToken).ConfigureAwait(false);
            note = " The damaged session file was discarded.";
        }

        RollbackReport replay = await journal.RollbackAllAsync(cancellationToken).ConfigureAwait(false);
        RollbackReport sweep = await journal.SweepAllAsync(cancellationToken).ConfigureAwait(false);
        int failures = replay.Failures.Count + sweep.Failures.Count;
        return failures == 0
            ? new MaintenanceResult(MaintenanceOutcome.Done, $"Rolled back {replay.Restored} change(s) and swept {sweep.Restored} marked artifact(s).{note}")
            : new MaintenanceResult(MaintenanceOutcome.Incomplete, $"{failures} change(s) could not be reverted; see the log.{note}");
    }
}
