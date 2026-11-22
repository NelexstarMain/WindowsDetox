using System.Globalization;
using Blocker.UI.Sdk;

namespace Blocker.UI.Controls.Rendering;

/// <summary>A day column with a time range, in minutes after midnight.</summary>
/// <param name="Day">Zero-based column.</param>
/// <param name="Start">Start minute.</param>
/// <param name="End">End minute (exclusive).</param>
internal readonly record struct TimeSlot(int Day, int Start, int End);

/// <summary>A rectangle in device-independent pixels; independent of WPF so the geometry stays unit-testable.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
internal readonly record struct RectD(double X, double Y, double Width, double Height)
{
    /// <summary>Gets the right edge.</summary>
    public double Right => X + Width;

    /// <summary>Gets the bottom edge.</summary>
    public double Bottom => Y + Height;
}

/// <summary>Where an event sits among the overlapping events of its day.</summary>
/// <param name="Column">Zero-based lane.</param>
/// <param name="Columns">Number of lanes in the overlap cluster the event belongs to.</param>
internal readonly record struct ColumnSlot(int Column, int Columns);

/// <summary>Position of the floating popover card.</summary>
/// <param name="X">Left edge in control coordinates.</param>
/// <param name="Y">Top edge in control coordinates.</param>
/// <param name="OnRight">Whether the card sits to the right of the anchor.</param>
internal readonly record struct PopoverPosition(double X, double Y, bool OnRight);

/// <summary>Pixel layout of the day columns: converts between pixels, days and minutes.</summary>
/// <param name="ColumnsLeft">X of the first column (the width of the hour gutter).</param>
/// <param name="ColumnsWidth">Total width of all day columns.</param>
/// <param name="DayCount">Number of day columns.</param>
/// <param name="HourHeight">Height of one hour in pixels.</param>
/// <param name="SnapMinutes">Drag granularity.</param>
internal readonly record struct TimeGridGeometry(double ColumnsLeft, double ColumnsWidth, int DayCount, double HourHeight, int SnapMinutes)
{
    /// <summary>Gets the width of one day column.</summary>
    public double ColumnWidth => DayCount > 0 ? ColumnsWidth / DayCount : 0;

    /// <summary>Gets the pixels per minute.</summary>
    public double PixelsPerMinute => HourHeight / 60;

    /// <summary>Converts a minute to a Y coordinate.</summary>
    /// <param name="minute">Minutes after midnight.</param>
    /// <returns>Pixels from the top of the grid.</returns>
    public double YOf(double minute) => minute * PixelsPerMinute;

    /// <summary>Converts a Y coordinate to minutes, clamped to the day.</summary>
    /// <param name="y">Pixels from the top of the grid.</param>
    /// <returns>Minutes after midnight in 0..1440.</returns>
    public double MinuteAt(double y) => Math.Clamp(y / PixelsPerMinute, 0, TimeGridMath.MinutesPerDay);

    /// <summary>Gets the left edge of a day column.</summary>
    /// <param name="day">Zero-based column.</param>
    /// <returns>X in pixels.</returns>
    public double ColumnX(int day) => ColumnsLeft + (day * ColumnWidth);

    /// <summary>Gets the day column under an X coordinate, clamped to the existing columns.</summary>
    /// <param name="x">Pixels from the left edge.</param>
    /// <returns>A column in 0..DayCount-1.</returns>
    public int DayAt(double x) =>
        DayCount <= 0 || ColumnWidth <= 0 ? 0 : Math.Clamp((int)Math.Floor((x - ColumnsLeft) / ColumnWidth), 0, DayCount - 1);
}

/// <summary>Pure geometry and snapping rules of the time grid: no WPF types, so everything here is unit-tested.</summary>
internal static class TimeGridMath
{
    /// <summary>Minutes in a day.</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>Height of one hour row.</summary>
    public const double HourHeight = 48;

    /// <summary>Width of the hour label gutter.</summary>
    public const double GutterWidth = 56;

    /// <summary>Height of the day header row.</summary>
    public const double HeaderHeight = 64;

    /// <summary>Viewport height of the scrolling body when the host gives no finite height.</summary>
    public const double DefaultViewportHeight = 720;

    /// <summary>Height of the bottom strip of an event that resizes it.</summary>
    public const double ResizeHandleHeight = 6;

    /// <summary>Pointer travel below which a press and release is a click.</summary>
    public const double DragThreshold = 4;

    /// <summary>Length of the block a plain click on empty space creates.</summary>
    public const int ClickCreateMinutes = 60;

    /// <summary>Smallest drawn height of an event, so a 5-minute block stays visible.</summary>
    public const double MinEventHeight = 14;

    /// <summary>Events lower than this show only their title.</summary>
    public const double DetailMinHeight = 36;

    /// <summary>Widest floating card.</summary>
    public const double PopoverMaxWidth = 340;

    /// <summary>Space between the anchor block and the popover card.</summary>
    public const double PopoverGap = 8;

    /// <summary>Smallest distance between the popover card and the control edge.</summary>
    public const double PopoverMargin = 8;

    /// <summary>Space kept free at the right of a day column so busy days can still be clicked to create a block.</summary>
    public const double ColumnRightGap = 6;

    /// <summary>Gets the full height of the 24-hour content.</summary>
    public static double ContentHeight => HourHeight * 24;

    /// <summary>Rounds minutes to the nearest snap step (halves away from zero).</summary>
    /// <param name="minutes">Raw minutes.</param>
    /// <param name="snap">Step in minutes.</param>
    /// <returns>A multiple of <paramref name="snap"/>.</returns>
    public static int Snap(double minutes, int snap) => (int)(Math.Round(minutes / snap, MidpointRounding.AwayFromZero) * snap);

    /// <summary>Rounds minutes down to a snap step.</summary>
    /// <param name="minutes">Raw minutes.</param>
    /// <param name="snap">Step in minutes.</param>
    /// <returns>A multiple of <paramref name="snap"/> not above <paramref name="minutes"/>.</returns>
    public static int SnapDown(double minutes, int snap) => (int)(Math.Floor(minutes / snap) * snap);

    /// <summary>Rounds minutes up to a snap step.</summary>
    /// <param name="minutes">Raw minutes.</param>
    /// <param name="snap">Step in minutes.</param>
    /// <returns>A multiple of <paramref name="snap"/> not below <paramref name="minutes"/>.</returns>
    public static int SnapUp(double minutes, int snap) => (int)(Math.Ceiling(minutes / snap) * snap);

    /// <summary>Gets the height of the scrolling body: fills a finite available height when it is taller than the default.</summary>
    /// <param name="availableHeight">Height the host offers the whole control (may be infinite).</param>
    /// <returns>The body viewport height.</returns>
    public static double ViewportHeight(double availableHeight) =>
        double.IsFinite(availableHeight) ? Math.Max(DefaultViewportHeight, availableHeight - HeaderHeight) : DefaultViewportHeight;

    /// <summary>Assigns every event a lane so overlapping events of one day sit side by side (Google Calendar style).</summary>
    /// <param name="events">The events; the result is indexed the same way.</param>
    /// <returns>One slot per event. Events that are not transitively overlapping keep the full width (<c>Columns == 1</c>).</returns>
    public static ColumnSlot[] PackColumns(IReadOnlyList<UiTimeGridEvent> events)
    {
        var slots = new ColumnSlot[events.Count];
        var order = new int[events.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => CompareForPacking(events[a], events[b], a, b));

        var laneEnds = new List<int>();
        var cluster = new List<int>();
        int clusterEnd = 0;
        int clusterDay = -1;
        foreach (int index in order)
        {
            UiTimeGridEvent e = events[index];
            if (cluster.Count > 0 && (e.Day != clusterDay || e.StartMinute >= clusterEnd))
            {
                Flush(cluster, laneEnds.Count, slots);
                cluster.Clear();
                laneEnds.Clear();
            }

            clusterDay = e.Day;
            int lane = laneEnds.FindIndex(end => end <= e.StartMinute);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(e.EndMinute);
            }
            else
            {
                laneEnds[lane] = e.EndMinute;
            }

            clusterEnd = cluster.Count == 0 ? e.EndMinute : Math.Max(clusterEnd, e.EndMinute);
            cluster.Add(index);
            slots[index] = new ColumnSlot(lane, 0);
        }

        Flush(cluster, laneEnds.Count, slots);
        return slots;
    }

    /// <summary>Computes the rectangle of a block.</summary>
    /// <param name="geometry">Column layout.</param>
    /// <param name="slot">Day and time range.</param>
    /// <param name="column">Lane among overlapping blocks.</param>
    /// <param name="columns">Number of lanes.</param>
    /// <returns>The block rectangle, one pixel inset so neighbours do not touch.</returns>
    public static RectD BlockRect(in TimeGridGeometry geometry, TimeSlot slot, int column, int columns)
    {
        double columnWidth = geometry.ColumnWidth;
        double usable = Math.Max(0, columnWidth - ColumnRightGap);
        double laneWidth = usable / Math.Max(columns, 1);
        double height = Math.Max(MinEventHeight, geometry.YOf(slot.End - slot.Start) - 2);
        return new RectD(geometry.ColumnX(slot.Day) + 1 + (column * laneWidth), geometry.YOf(slot.Start) + 1, Math.Max(0, laneWidth - 1), height);
    }

    /// <summary>Gets whether a block of the given height has room for a second text line.</summary>
    /// <param name="height">Block height in pixels.</param>
    /// <returns><see langword="true"/> when the detail line is shown.</returns>
    public static bool ShowsDetail(double height) => height >= DetailMinHeight;

    /// <summary>Computes the range of a block created by dragging on empty space.</summary>
    /// <param name="anchorMinute">Raw minute under the press.</param>
    /// <param name="currentMinute">Raw minute under the pointer now (either side of the anchor).</param>
    /// <param name="snap">Snap step.</param>
    /// <returns>Start and end, snapped, at least one snap step long, inside 0..1440.</returns>
    public static (int Start, int End) CreateRange(double anchorMinute, double currentMinute, int snap)
    {
        double low = Math.Min(anchorMinute, currentMinute);
        double high = Math.Max(anchorMinute, currentMinute);
        int start = SnapDown(low, snap);
        int end = Math.Max(SnapUp(high, snap), start + snap);
        if (end > MinutesPerDay)
        {
            end = MinutesPerDay;
            start = Math.Min(start, MinutesPerDay - snap);
        }

        return (Math.Max(start, 0), end);
    }

    /// <summary>Computes the one-hour block a plain click creates.</summary>
    /// <param name="clickMinute">Raw minute under the click.</param>
    /// <param name="snap">Snap step.</param>
    /// <returns>The snapped slot under the pointer plus one hour, shifted up when it would pass midnight.</returns>
    public static (int Start, int End) ClickCreateRange(double clickMinute, int snap)
    {
        int start = Math.Clamp(SnapDown(clickMinute, snap), 0, MinutesPerDay - ClickCreateMinutes);
        return (start, start + ClickCreateMinutes);
    }

    /// <summary>Moves a block in time, keeping its duration.</summary>
    /// <param name="start">Original start.</param>
    /// <param name="end">Original end.</param>
    /// <param name="deltaMinutes">Raw pointer travel in minutes.</param>
    /// <param name="snap">Snap step.</param>
    /// <returns>The new start and end, inside 0..1440.</returns>
    public static (int Start, int End) MoveRange(int start, int end, double deltaMinutes, int snap)
    {
        int duration = end - start;
        int moved = Math.Clamp(Snap(start + deltaMinutes, snap), 0, MinutesPerDay - duration);
        return (moved, moved + duration);
    }

    /// <summary>Resizes the end of a block.</summary>
    /// <param name="start">Start, which does not change.</param>
    /// <param name="end">Original end.</param>
    /// <param name="deltaMinutes">Raw pointer travel in minutes.</param>
    /// <param name="snap">Snap step.</param>
    /// <returns>The new end: snapped, at least one snap step after the start, at most 1440.</returns>
    public static int ResizeEnd(int start, int end, double deltaMinutes, int snap) =>
        Math.Min(MinutesPerDay, Math.Max(Snap(end + deltaMinutes, snap), start + snap));

    /// <summary>Formats a minute as <c>HH:mm</c> (24-hour, invariant); 1440 is <c>24:00</c>.</summary>
    /// <param name="minute">Minutes after midnight.</param>
    /// <returns>The text.</returns>
    public static string FormatTime(int minute) =>
        string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");

    /// <summary>Formats a time range for the ghost label.</summary>
    /// <param name="start">Start minute.</param>
    /// <param name="end">End minute.</param>
    /// <returns>For example <c>09:00 - 10:30</c> with an en dash.</returns>
    public static string FormatRange(int start, int end) => FormatTime(start) + " – " + FormatTime(end);

    /// <summary>Places the popover card next to its anchor: right side when it fits, otherwise left, always inside the bounds.</summary>
    /// <param name="anchor">Anchor block in control coordinates.</param>
    /// <param name="width">Card width.</param>
    /// <param name="height">Card height.</param>
    /// <param name="boundsWidth">Control width.</param>
    /// <param name="boundsHeight">Control height.</param>
    /// <returns>The card position.</returns>
    public static PopoverPosition PlacePopover(RectD anchor, double width, double height, double boundsWidth, double boundsHeight)
    {
        double rightX = anchor.Right + PopoverGap;
        double leftX = anchor.X - PopoverGap - width;
        bool fitsRight = rightX + width + PopoverMargin <= boundsWidth;
        bool fitsLeft = leftX >= PopoverMargin;
        bool onRight = fitsRight || (!fitsLeft && boundsWidth - anchor.Right >= anchor.X);
        double x = ClampInside(onRight ? rightX : leftX, width, boundsWidth);
        double y = ClampInside(anchor.Y, height, boundsHeight);
        return new PopoverPosition(x, y, onRight);
    }

    /// <summary>Centres the popover in the bounds, used when the anchor block is not known.</summary>
    /// <param name="width">Card width.</param>
    /// <param name="height">Card height.</param>
    /// <param name="boundsWidth">Control width.</param>
    /// <param name="boundsHeight">Control height.</param>
    /// <returns>The card position.</returns>
    public static PopoverPosition CenterPopover(double width, double height, double boundsWidth, double boundsHeight) =>
        new(ClampInside((boundsWidth - width) / 2, width, boundsWidth), ClampInside((boundsHeight - height) / 2, height, boundsHeight), true);

    private static double ClampInside(double position, double size, double bounds)
    {
        double max = bounds - size - PopoverMargin;
        return Math.Max(PopoverMargin, Math.Min(position, max));
    }

    private static int CompareForPacking(UiTimeGridEvent a, UiTimeGridEvent b, int indexA, int indexB)
    {
        int byDay = a.Day.CompareTo(b.Day);
        if (byDay != 0)
        {
            return byDay;
        }

        int byStart = a.StartMinute.CompareTo(b.StartMinute);
        if (byStart != 0)
        {
            return byStart;
        }

        int byLength = b.EndMinute.CompareTo(a.EndMinute);
        return byLength != 0 ? byLength : indexA.CompareTo(indexB);
    }

    private static void Flush(List<int> cluster, int columns, ColumnSlot[] slots)
    {
        foreach (int index in cluster)
        {
            slots[index] = new ColumnSlot(slots[index].Column, columns);
        }
    }
}

/// <summary>Builds the actions the time grid delivers to plugins.</summary>
internal static class TimeGridActions
{
    /// <summary>Creates a Schedule action carrying <c>day</c>, <c>start</c> and <c>end</c> as invariant integer strings.</summary>
    /// <param name="elementId">Grid id (new block) or block id (move, resize).</param>
    /// <param name="slot">The new position.</param>
    /// <returns>The action.</returns>
    public static UiAction Schedule(string elementId, TimeSlot slot) => new(
        elementId,
        UiActionKind.Schedule,
        null,
        new Dictionary<string, string>(3)
        {
            ["day"] = slot.Day.ToString(CultureInfo.InvariantCulture),
            ["start"] = slot.Start.ToString(CultureInfo.InvariantCulture),
            ["end"] = slot.End.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>Creates a click on a block or header; <paramref name="extend"/> adds <c>extend=true</c> for Ctrl/Shift clicks.</summary>
    /// <param name="elementId">Block or day id.</param>
    /// <param name="extend">Whether Ctrl or Shift was held.</param>
    /// <returns>The action.</returns>
    public static UiAction Click(string elementId, bool extend) => extend
        ? new UiAction(elementId, UiActionKind.Click, null, new Dictionary<string, string>(1) { ["extend"] = "true" })
        : UiAction.Click(elementId);
}

/// <summary>How many month-cell chips fit under the day number.</summary>
internal static class MonthChipMath
{
    /// <summary>Height of one chip.</summary>
    public const double ChipHeight = 17;

    /// <summary>Gap between chips.</summary>
    public const double ChipGap = 2;

    /// <summary>Height of the "+N" overflow line.</summary>
    public const double OverflowHeight = 14;

    /// <summary>Gets the number of chips to draw so that they, and the overflow line when some are left out, fit the height.</summary>
    /// <param name="total">Chips offered by the plugin.</param>
    /// <param name="availableHeight">Height under the day number.</param>
    /// <returns>A count in 0..<paramref name="total"/>; the remainder is shown as "+N".</returns>
    public static int VisibleChips(int total, double availableHeight)
    {
        const double chip = ChipHeight + ChipGap;
        if (total * chip <= availableHeight + ChipGap)
        {
            return total;
        }

        int fit = (int)Math.Floor((availableHeight - OverflowHeight) / chip);
        return Math.Clamp(fit, 0, total);
    }
}
