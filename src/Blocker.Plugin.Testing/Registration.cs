namespace Blocker.Plugin.Testing;

/// <summary>Idempotent <see cref="IDisposable"/> that runs a callback once.</summary>
internal sealed class Registration(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}
