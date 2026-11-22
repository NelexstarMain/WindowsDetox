using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>What a pointer press on the time grid started.</summary>
internal enum TimeGridGestureKind
{
    /// <summary>Empty space: a drag draws a new block, a click creates a one-hour block.</summary>
    Create,

    /// <summary>The body of a movable block.</summary>
    Move,

    /// <summary>The bottom edge of a resizable block.</summary>
    Resize,

    /// <summary>A block that can neither move nor resize here: only a click is meaningful.</summary>
    Press,
}

/// <summary>
/// One press-drag-release on the time grid as a small state machine with no WPF dependency: feed it pointer positions and it keeps the
/// snapped preview slot, then turns the release into the action for the plugin. It allocates only when the action is built.
/// </summary>
internal sealed class TimeGridGesture
{
    private readonly double _pressX;
    private readonly double _pressY;
    private readonly double _pressMinute;
    private readonly int _pressDay;
    private TimeSlot _current;

    /// <summary>Initializes a new instance of the <see cref="TimeGridGesture"/> class.</summary>
    /// <param name="kind">What was pressed.</param>
    /// <param name="targetId">Grid id for <see cref="TimeGridGestureKind.Create"/>, otherwise the block id.</param>
    /// <param name="origin">Position of the block before the gesture; for create, the pressed day with an empty range.</param>
    /// <param name="pressX">Pointer X at press.</param>
    /// <param name="pressY">Pointer Y at press.</param>
    /// <param name="geometry">Column layout at press time.</param>
    public TimeGridGesture(TimeGridGestureKind kind, string targetId, TimeSlot origin, double pressX, double pressY, in TimeGridGeometry geometry)
    {
        Kind = kind;
        TargetId = targetId;
        Origin = origin;
        _current = origin;
        _pressX = pressX;
        _pressY = pressY;
        _pressMinute = geometry.MinuteAt(pressY);
        _pressDay = geometry.DayAt(pressX);
    }

    /// <summary>Gets what was pressed.</summary>
    public TimeGridGestureKind Kind { get; }

    /// <summary>Gets the grid id (create) or block id.</summary>
    public string TargetId { get; }

    /// <summary>Gets the block position before the gesture.</summary>
    public TimeSlot Origin { get; }

    /// <summary>Gets whether the pointer travelled past the drag threshold (so the release is no longer a click).</summary>
    public bool Moved { get; private set; }

    /// <summary>Gets the current preview slot: for create, only meaningful once <see cref="Moved"/>.</summary>
    public TimeSlot Current => _current;

    /// <summary>Feeds a pointer position.</summary>
    /// <param name="x">Pointer X in grid coordinates.</param>
    /// <param name="y">Pointer Y in grid coordinates.</param>
    /// <param name="geometry">Column layout now.</param>
    /// <returns><see langword="true"/> when the preview changed and must be redrawn.</returns>
    public bool Update(double x, double y, in TimeGridGeometry geometry)
    {
        bool justMoved = false;
        if (!Moved)
        {
            double dx = x - _pressX;
            double dy = y - _pressY;
            if ((dx * dx) + (dy * dy) < TimeGridMath.DragThreshold * TimeGridMath.DragThreshold)
            {
                return false;
            }

            Moved = true;
            justMoved = true;
        }

        TimeSlot next = Kind switch
        {
            TimeGridGestureKind.Create => CreateSlot(y, geometry),
            TimeGridGestureKind.Move => MoveSlot(x, y, geometry),
            TimeGridGestureKind.Resize => ResizeSlot(y, geometry),
            _ => _current,
        };

        bool changed = next != _current;
        _current = next;
        return (changed || justMoved) && Kind != TimeGridGestureKind.Press;
    }

    /// <summary>Turns the release into an action.</summary>
    /// <param name="extend">Whether Ctrl or Shift is held (only matters for a click on a block).</param>
    /// <param name="geometry">Column layout at release time.</param>
    /// <returns>The action, or <see langword="null"/> when the drag changed nothing.</returns>
    public UiAction? Complete(bool extend, in TimeGridGeometry geometry)
    {
        if (!Moved)
        {
            if (Kind != TimeGridGestureKind.Create)
            {
                return TimeGridActions.Click(TargetId, extend);
            }

            (int start, int end) = TimeGridMath.ClickCreateRange(_pressMinute, geometry.SnapMinutes);
            return TimeGridActions.Schedule(TargetId, new TimeSlot(_pressDay, start, end));
        }

        bool changed = Kind == TimeGridGestureKind.Create || _current != Origin;
        return Kind != TimeGridGestureKind.Press && changed ? TimeGridActions.Schedule(TargetId, _current) : null;
    }

    private TimeSlot CreateSlot(double y, in TimeGridGeometry geometry)
    {
        (int start, int end) = TimeGridMath.CreateRange(_pressMinute, geometry.MinuteAt(y), geometry.SnapMinutes);
        return new TimeSlot(_pressDay, start, end);
    }

    private TimeSlot MoveSlot(double x, double y, in TimeGridGeometry geometry)
    {
        int day = Math.Clamp(Origin.Day + (geometry.DayAt(x) - _pressDay), 0, Math.Max(geometry.DayCount - 1, 0));
        (int start, int end) = TimeGridMath.MoveRange(Origin.Start, Origin.End, (y - _pressY) / geometry.PixelsPerMinute, geometry.SnapMinutes);
        return new TimeSlot(day, start, end);
    }

    private TimeSlot ResizeSlot(double y, in TimeGridGeometry geometry) =>
        Origin with { End = TimeGridMath.ResizeEnd(Origin.Start, Origin.End, (y - _pressY) / geometry.PixelsPerMinute, geometry.SnapMinutes) };
}
