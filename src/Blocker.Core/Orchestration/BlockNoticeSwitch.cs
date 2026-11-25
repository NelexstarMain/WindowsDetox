namespace Blocker.Core.Orchestration;

/// <summary>
/// Whether a blocked program brings up the block notice. Read on every block and flipped by <c>settings.set</c>, so it is shared by the
/// coordinator, the app-rule enforcer and the block screen's info request. Turning it off only hides the notice; blocking is unchanged.
/// </summary>
public sealed class BlockNoticeSwitch
{
    private volatile bool _enabled = true;

    /// <summary>Gets or sets whether the notice is shown; on by default.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
