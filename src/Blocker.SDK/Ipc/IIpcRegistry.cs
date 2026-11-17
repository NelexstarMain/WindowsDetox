namespace Blocker.Sdk.Ipc;

/// <summary>
/// Requires <see cref="PluginCapability.Ipc"/>. Exposes plugin methods over the Blocker named pipe as
/// <c>plugin.{pluginId}.{method}</c>. Callers are authenticated by the service; payloads are size-limited
/// JSON and must be validated by the handler like any untrusted input.
/// </summary>
public interface IIpcRegistry
{
    /// <summary>Registers an IPC method.</summary>
    /// <param name="method">Method name: starts with a lowercase letter, then letters or digits, at most 64 characters.</param>
    /// <param name="handler">Receives the request JSON and returns the response JSON.</param>
    /// <returns>A registration; dispose it to remove the method.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is invalid or already registered.</exception>
    IDisposable Register(string method, Func<string, CancellationToken, ValueTask<string>> handler);
}
