namespace Blocker.Sdk;

/// <summary>
/// Entry point of a Blocker plugin. The type implementing this interface is named in the
/// <c>entryType</c> field of <c>plugin.json</c> and must have a public parameterless constructor.
/// </summary>
/// <remarks>
/// Plugins run inside the sandboxed <c>Blocker.PluginHost</c> process, never inside the SYSTEM service or
/// the UI process. All callbacks may be invoked from thread-pool threads and must honour cancellation.
/// </remarks>
public interface IBlockerPlugin
{
    /// <summary>
    /// Called once after the plugin is loaded. Register extensions, event subscriptions and IPC methods here.
    /// </summary>
    /// <param name="context">Host services available to this plugin.</param>
    /// <param name="cancellationToken">Signalled when the host aborts initialization.</param>
    /// <returns>A task that completes when the plugin is ready.</returns>
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Called once before the plugin is unloaded. Flush state to <see cref="Storage.IPluginStorage"/> here.
    /// Registrations made through the context are removed automatically afterwards.
    /// </summary>
    /// <param name="cancellationToken">Signalled when the host stops waiting for shutdown.</param>
    /// <returns>A task that completes when the plugin has released its resources.</returns>
    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}
