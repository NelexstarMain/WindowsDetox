using Blocker.Core.Supervision;
using Microsoft.Extensions.Hosting;

namespace Blocker.Watchdog;

/// <summary>Runs the <see cref="MutualSupervisor"/> for the lifetime of the service.</summary>
internal sealed class WatchdogWorker(MutualSupervisor supervisor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => supervisor.RunAsync(stoppingToken);
}
