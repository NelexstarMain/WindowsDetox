namespace Blocker.UI.Views;

/// <summary>
/// Turns raw pointer enter and leave into calm open and close decisions for a flyout: pointing at something only counts once the pointer
/// has stayed for <c>openDelay</c>, and leaving only counts after <c>closeDelay</c>, so crossing the gap between an item and its flyout,
/// or brushing past other items, does not flicker. Time is passed in, so the behaviour is deterministic.
/// </summary>
/// <typeparam name="T">What can be pointed at.</typeparam>
internal sealed class HoverIntent<T>
    where T : class
{
    private readonly TimeSpan _openDelay;
    private readonly TimeSpan _closeDelay;
    private T? _wanted;
    private TimeSpan _since;

    /// <summary>Creates the tracker.</summary>
    /// <param name="openDelay">How long the pointer must stay on something before it counts.</param>
    /// <param name="closeDelay">How long the pointer must stay away before it stops counting.</param>
    public HoverIntent(TimeSpan openDelay, TimeSpan closeDelay)
    {
        _openDelay = openDelay;
        _closeDelay = closeDelay;
    }

    /// <summary>Gets what currently counts as pointed at, or <see langword="null"/>.</summary>
    public T? Current { get; private set; }

    /// <summary>Gets whether a change is waiting for its delay to pass.</summary>
    public bool IsPending => !Equals(_wanted, Current);

    /// <summary>Reports where the pointer is now.</summary>
    /// <param name="target">What the pointer is over, or <see langword="null"/> when it is over nothing that belongs to the flyout.</param>
    /// <param name="now">The current time.</param>
    public void Point(T? target, TimeSpan now)
    {
        if (!Equals(_wanted, target))
        {
            (_wanted, _since) = (target, now);
        }
    }

    /// <summary>Makes something count immediately, for example when it gets keyboard focus.</summary>
    /// <param name="target">What to count, or <see langword="null"/> to stop counting anything.</param>
    public void Force(T? target) => (_wanted, Current) = (target, target);

    /// <summary>Applies a pending change once its delay has passed.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when <see cref="Current"/> changed.</returns>
    public bool Update(TimeSpan now)
    {
        if (!IsPending || now - _since < (_wanted is null ? _closeDelay : _openDelay))
        {
            return false;
        }

        Current = _wanted;
        return true;
    }
}
