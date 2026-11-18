using Blocker.Core.ServiceHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.Service;

/// <summary>Runs <see cref="BlockerServiceApp"/> for the lifetime of the host. Stopping the host stops all activity but never rolls an active session back.</summary>
/// <param name="paths">File locations.</param>
/// <param name="devMode">Whether developer abort is enabled.</param>
/// <param name="loggers">Logger factory.</param>
internal sealed partial class BlockerWorker(BlockerPaths paths, bool devMode, ILoggerFactory loggers) : IHostedService
{
    private readonly ILogger _logger = loggers.CreateLogger<BlockerWorker>();
    private BlockerServiceApp? _app;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _app = await BlockerServiceApp.StartAsync(WindowsServicePorts.Create(paths, devMode, loggers), paths, loggers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rethrown: the host fails to start, the process exits non-zero and the SCM failure actions restart it.
            LogStartFailed(_logger, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is { } app)
        {
            _app = null;
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The Blocker service could not start.")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);
}
