namespace Blocker.UI.Views;

/// <summary>A rectangle in the board's coordinates; the panel's animation state is kept in these so it needs no UI types.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
internal readonly record struct Box(double X, double Y, double Width, double Height)
{
    /// <summary>Gets the point between two boxes.</summary>
    /// <param name="from">The box at <paramref name="t"/> 0.</param>
    /// <param name="to">The box at <paramref name="t"/> 1.</param>
    /// <param name="t">Progress, 0 to 1.</param>
    /// <returns>The interpolated box.</returns>
    public static Box Lerp(Box from, Box to, double t) => new(
        from.X + ((to.X - from.X) * t),
        from.Y + ((to.Y - from.Y) * t),
        from.Width + ((to.Width - from.Width) * t),
        from.Height + ((to.Height - from.Height) * t));

    /// <summary>Gets whether every edge of another box is within half a unit of this one.</summary>
    /// <param name="other">The other box.</param>
    /// <returns><see langword="true"/> when they are visually the same.</returns>
    public bool IsNear(Box other) =>
        Math.Abs(X - other.X) <= 0.5 && Math.Abs(Y - other.Y) <= 0.5 && Math.Abs(Width - other.Width) <= 0.5 && Math.Abs(Height - other.Height) <= 0.5;
}

/// <summary>
/// Tracks where each item of the board is drawn while it moves to a new slot. A child that is already moving and gets a new target
/// continues from where it is drawn right now (so changes in mid-flight never stack or jump), and a child seen for the first time just
/// appears in place. Time is passed in, so the behaviour is deterministic.
/// </summary>
/// <typeparam name="TKey">Identifies an item.</typeparam>
internal sealed class ReflowAnimator<TKey>
    where TKey : notnull
{
    private readonly TimeSpan _duration;
    private readonly Func<double, double> _ease;
    private readonly Dictionary<TKey, Track> _tracks = [];

    /// <summary>Creates the animator.</summary>
    /// <param name="duration">How long a move takes; zero or less makes every move instant.</param>
    /// <param name="ease">Maps linear progress (0 to 1) to eased progress.</param>
    public ReflowAnimator(TimeSpan duration, Func<double, double> ease)
    {
        _duration = duration;
        _ease = ease;
    }

    /// <summary>Gets whether an item has been seen before.</summary>
    /// <param name="key">The item.</param>
    /// <returns><see langword="true"/> when known.</returns>
    public bool Contains(TKey key) => _tracks.ContainsKey(key);

    /// <summary>Gets where an item is drawn at a moment in time.</summary>
    /// <param name="key">The item; must be known.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The box.</returns>
    public Box Current(TKey key, TimeSpan now) => Sample(_tracks[key], now);

    /// <summary>Moves an item towards a (possibly new) target, continuing from where it is drawn now.</summary>
    /// <param name="key">The item.</param>
    /// <param name="target">Where the item belongs.</param>
    /// <param name="now">The current time.</param>
    /// <param name="animate">Whether to slide; <see langword="false"/> jumps there (first layout, window resizing).</param>
    /// <returns>Where to draw the item now.</returns>
    public Box Retarget(TKey key, Box target, TimeSpan now, bool animate)
    {
        if (!_tracks.TryGetValue(key, out Track? track))
        {
            _tracks[key] = new Track(target, target, now);
            return target;
        }

        if (track.To.IsNear(target))
        {
            // Same destination: leave a running move alone.
            if (track.From == track.To)
            {
                _tracks[key] = new Track(target, target, now);
                return target;
            }

            track.To = target;
            return Sample(track, now);
        }

        Box from = Sample(track, now);
        Track next = animate && _duration > TimeSpan.Zero ? new Track(from, target, now) : new Track(target, target, now);
        _tracks[key] = next;
        return Sample(next, now);
    }

    /// <summary>Pins an item to a box without animating, for example while it is dragged by the pointer.</summary>
    /// <param name="key">The item.</param>
    /// <param name="box">Where it is drawn.</param>
    /// <param name="now">The current time.</param>
    public void Hold(TKey key, Box box, TimeSpan now) => _tracks[key] = new Track(box, box, now);

    /// <summary>Keeps only the given items.</summary>
    /// <param name="keep">Items that stay.</param>
    public void Prune(IReadOnlyCollection<TKey> keep)
    {
        foreach (TKey gone in _tracks.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _tracks.Remove(gone);
        }
    }

    /// <summary>Gets whether any item is still moving.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> while a move is running.</returns>
    public bool IsAnimating(TimeSpan now) =>
        _duration > TimeSpan.Zero && _tracks.Values.Any(t => t.From != t.To && now - t.Start < _duration);

    private Box Sample(Track track, TimeSpan now)
    {
        if (_duration <= TimeSpan.Zero || track.From == track.To)
        {
            return track.To;
        }

        double progress = (now - track.Start) / _duration;
        return progress >= 1 ? track.To : Box.Lerp(track.From, track.To, _ease(Math.Max(0, progress)));
    }

    private sealed class Track(Box from, Box to, TimeSpan start)
    {
        public Box From { get; } = from;

        public Box To { get; set; } = to;

        public TimeSpan Start { get; } = start;
    }
}
