using Blocker.Sdk.Session;

namespace Blocker.Plugin.Testing;

/// <summary>Settable <see cref="ISessionInfo"/> for tests. Starts as <see cref="SessionSnapshot.Idle"/>.</summary>
public sealed class FakeSessionInfo : ISessionInfo
{
    private volatile SessionSnapshot _current = SessionSnapshot.Idle;

    /// <summary>Gets or sets the snapshot returned to the plugin.</summary>
    public SessionSnapshot Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}
