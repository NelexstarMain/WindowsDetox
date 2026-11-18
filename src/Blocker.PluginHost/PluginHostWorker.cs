using Blocker.Core.PluginRuntime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.PluginHost;

/// <summary>Serves the service's requests until the pipe closes or the service asks the host to shut down, then ends the process.</summary>
internal sealed partial class PluginHostWorker(PluginHostEndpoint endpoint, IHostApplicationLifetime lifetime, ILogger<PluginHostWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Environment.ExitCode = await endpoint.RunAsync(stoppingToken).ConfigureAwait(false) ? 0 : ExitCodes.Refused;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            LogConnectionFailed(logger, ex);
            Environment.ExitCode = ExitCodes.ConnectionFailed;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopping(logger);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The plugin host could not reach the service.")]
    private static partial void LogConnectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The plugin host is stopping.")]
    private static partial void LogStopping(ILogger logger);
}
