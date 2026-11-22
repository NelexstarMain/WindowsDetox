namespace Blocker.UI.Sdk;

/// <summary>
/// Fluent factory for building declarative UI trees, for example:
/// <code>
/// Ui.Card("Focus", Ui.Stat("Blocked today", "12", tone: UiTone.Accent), Ui.Button("reset", "Reset"))
/// </code>
/// </summary>
public static class Ui
{
    /// <summary>Creates a page.</summary>
    /// <param name="title">Page title.</param>
    /// <param name="children">Content.</param>
    /// <returns>The page.</returns>
    public static UiPage Page(string title, params UiNode[] children) => new(title, null, children);

    /// <summary>Creates a page with a subtitle.</summary>
    /// <param name="title">Page title.</param>
    /// <param name="subtitle">Subtitle.</param>
    /// <param name="children">Content.</param>
    /// <returns>The page.</returns>
    public static UiPage Page(string title, string subtitle, params UiNode[] children) => new(title, subtitle, children);

    /// <summary>Creates a titled card.</summary>
    /// <param name="title">Card title.</param>
    /// <param name="children">Content.</param>
    /// <returns>The card.</returns>
    public static UiCard Card(string title, params UiNode[] children) => new(children, title);

    /// <summary>Creates an untitled card.</summary>
    /// <param name="children">Content.</param>
    /// <returns>The card.</returns>
    public static UiCard Card(params UiNode[] children) => new(children);

    /// <summary>Creates a vertical stack.</summary>
    /// <param name="children">Content.</param>
    /// <returns>The stack.</returns>
    public static UiStack Column(params UiNode[] children) => new(children);

    /// <summary>Creates a horizontal stack.</summary>
    /// <param name="children">Content.</param>
    /// <returns>The stack.</returns>
    public static UiStack Row(params UiNode[] children) => new(children, UiOrientation.Horizontal);

    /// <summary>Creates a grid.</summary>
    /// <param name="columns">Column count (1-6).</param>
    /// <param name="children">Content.</param>
    /// <returns>The grid.</returns>
    public static UiGrid Grid(int columns, params UiNode[] children) => new(children, columns);

    /// <summary>Creates a text element.</summary>
    /// <param name="text">Content.</param>
    /// <param name="style">Typographic role.</param>
    /// <param name="tone">Colour role.</param>
    /// <returns>The text element.</returns>
    public static UiText Text(string text, UiTextStyle style = UiTextStyle.Body, UiTone tone = UiTone.Neutral) => new(text, style, tone);

    /// <summary>Creates a stat tile.</summary>
    /// <param name="label">Label.</param>
    /// <param name="value">Pre-formatted value.</param>
    /// <param name="caption">Optional caption.</param>
    /// <param name="tone">Colour role.</param>
    /// <param name="icon">Optional icon.</param>
    /// <returns>The tile.</returns>
    public static UiStatTile Stat(string label, string value, string? caption = null, UiTone tone = UiTone.Neutral, UiIcon? icon = null) =>
        new(label, value, caption, tone, icon);

    /// <summary>Creates a progress bar.</summary>
    /// <param name="value">Current value.</param>
    /// <param name="maximum">Maximum value.</param>
    /// <param name="label">Optional label.</param>
    /// <returns>The progress bar.</returns>
    public static UiProgress Progress(double value, double maximum, string? label = null) => new(value, maximum, label);

    /// <summary>Creates a bar chart.</summary>
    /// <param name="title">Chart title.</param>
    /// <param name="points">Bars.</param>
    /// <returns>The chart.</returns>
    public static UiBarChart BarChart(string title, params UiDataPoint[] points) => new(points, title);

    /// <summary>Creates a donut chart.</summary>
    /// <param name="title">Chart title.</param>
    /// <param name="slices">Slices (at most 6).</param>
    /// <returns>The chart.</returns>
    public static UiDonutChart DonutChart(string title, params UiDataPoint[] slices) => new(slices, title);

    /// <summary>Creates a vector logo.</summary>
    /// <param name="pathData">Path geometry (SVG/WPF path syntax).</param>
    /// <param name="size">Edge length (12-96).</param>
    /// <returns>The glyph.</returns>
    public static UiGlyph Glyph(string pathData, double size = 32) => new(pathData, size);

    /// <summary>Creates a data point.</summary>
    /// <param name="label">Label.</param>
    /// <param name="value">Value.</param>
    /// <returns>The point.</returns>
    public static UiDataPoint Point(string label, double value) => new(label, value);

    /// <summary>Creates a button.</summary>
    /// <param name="id">Unique element id.</param>
    /// <param name="label">Text.</param>
    /// <param name="variant">Visual weight.</param>
    /// <param name="icon">Optional icon.</param>
    /// <returns>The button.</returns>
    public static UiButton Button(string id, string label, UiButtonVariant variant = UiButtonVariant.Secondary, UiIcon? icon = null) =>
        new(id, label, variant, icon);

    /// <summary>Creates a toggle.</summary>
    /// <param name="id">Unique element id.</param>
    /// <param name="label">Label.</param>
    /// <param name="isOn">Current state.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The toggle.</returns>
    public static UiToggle Toggle(string id, string label, bool isOn, string? description = null) => new(id, label, isOn, description);

    /// <summary>Creates a form.</summary>
    /// <param name="id">Unique element id.</param>
    /// <param name="submitLabel">Submit button text.</param>
    /// <param name="fields">Fields.</param>
    /// <returns>The form.</returns>
    public static UiForm Form(string id, string submitLabel, params UiFormField[] fields) => new(id, fields, submitLabel);

    /// <summary>Creates a month grid.</summary>
    /// <param name="weekdayLabels">Seven column headings.</param>
    /// <param name="cells">Day cells, row by row (a multiple of seven).</param>
    /// <param name="title">Optional month title.</param>
    /// <param name="previousId">Optional id of the previous button (with <paramref name="nextId"/>).</param>
    /// <param name="nextId">Optional id of the next button (with <paramref name="previousId"/>).</param>
    /// <param name="isCompact">Small cells for widgets.</param>
    /// <returns>The month grid.</returns>
    public static UiMonthGrid MonthGrid(
        IReadOnlyList<string> weekdayLabels,
        IReadOnlyList<UiMonthCell> cells,
        string? title = null,
        string? previousId = null,
        string? nextId = null,
        bool isCompact = false) => new(weekdayLabels, cells, title, previousId, nextId, isCompact);

    /// <summary>Creates a day or week schedule.</summary>
    /// <param name="id">Grid id; receives Schedule actions for blocks drawn on empty space.</param>
    /// <param name="days">One to seven columns.</param>
    /// <param name="events">Timed blocks.</param>
    /// <param name="nowMinute">Optional current time, minutes after midnight.</param>
    /// <returns>The time grid.</returns>
    public static UiTimeGrid TimeGrid(string id, IReadOnlyList<UiTimeGridDay> days, IReadOnlyList<UiTimeGridEvent> events, int? nowMinute = null) =>
        new(id, days, events, NowMinute: nowMinute);
}
