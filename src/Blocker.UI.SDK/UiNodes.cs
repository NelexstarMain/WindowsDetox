using System.Text.Json.Serialization;

namespace Blocker.UI.Sdk;

/// <summary>
/// Base of the declarative UI tree. A tree is plain, serializable data: plugins describe <em>what</em> to show
/// and the host renders it with its own controls, palette and typography. Plugins cannot
/// supply XAML, colours, fonts or sizes. Build trees with the <see cref="Ui"/> factory.
/// </summary>
/// <remarks>Inheritance is used here only because polymorphic JSON serialization requires a common base.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(UiPage), "page")]
[JsonDerivedType(typeof(UiStack), "stack")]
[JsonDerivedType(typeof(UiGrid), "grid")]
[JsonDerivedType(typeof(UiCard), "card")]
[JsonDerivedType(typeof(UiText), "text")]
[JsonDerivedType(typeof(UiStatTile), "stat")]
[JsonDerivedType(typeof(UiProgress), "progress")]
[JsonDerivedType(typeof(UiBarChart), "barChart")]
[JsonDerivedType(typeof(UiLineChart), "lineChart")]
[JsonDerivedType(typeof(UiDonutChart), "donutChart")]
[JsonDerivedType(typeof(UiHeatmap), "heatmap")]
[JsonDerivedType(typeof(UiMonthGrid), "monthGrid")]
[JsonDerivedType(typeof(UiTimeGrid), "timeGrid")]
[JsonDerivedType(typeof(UiGlyph), "glyph")]
[JsonDerivedType(typeof(UiTable), "table")]
[JsonDerivedType(typeof(UiButton), "button")]
[JsonDerivedType(typeof(UiToggle), "toggle")]
[JsonDerivedType(typeof(UiForm), "form")]
public abstract record UiNode;

/// <summary>A full navigation page. Only valid as the root returned by <c>IUiPagePlugin</c>.</summary>
/// <param name="Title">Page title.</param>
/// <param name="Subtitle">Optional description under the title.</param>
/// <param name="Children">Page content, laid out vertically.</param>
public sealed record UiPage(string Title, string? Subtitle, IReadOnlyList<UiNode> Children) : UiNode;

/// <summary>Lays out children in a row or column.</summary>
/// <param name="Children">Child nodes.</param>
/// <param name="Orientation">Direction.</param>
/// <param name="Gap">Spacing between children.</param>
public sealed record UiStack(IReadOnlyList<UiNode> Children, UiOrientation Orientation = UiOrientation.Vertical, UiGap Gap = UiGap.Medium) : UiNode;

/// <summary>Lays out children in a responsive grid of equal columns.</summary>
/// <param name="Children">Child nodes, filled row by row.</param>
/// <param name="Columns">Number of columns, 1-6.</param>
public sealed record UiGrid(IReadOnlyList<UiNode> Children, int Columns) : UiNode;

/// <summary>A raised card. Cards take part in the metaball layer and merge smoothly with the cursor field.</summary>
/// <param name="Children">Card content.</param>
/// <param name="Title">Optional header.</param>
/// <param name="Subtitle">Optional secondary header.</param>
/// <param name="Icon">Optional header icon.</param>
public sealed record UiCard(IReadOnlyList<UiNode> Children, string? Title = null, string? Subtitle = null, UiIcon? Icon = null) : UiNode;

/// <summary>A run of text.</summary>
/// <param name="Text">Content; plain text only, never interpreted as markup.</param>
/// <param name="Style">Typographic role.</param>
/// <param name="Tone">Colour role.</param>
public sealed record UiText(string Text, UiTextStyle Style = UiTextStyle.Body, UiTone Tone = UiTone.Neutral) : UiNode;

/// <summary>A single key figure with a label, rendered with a monospace value.</summary>
/// <param name="Label">What the value measures.</param>
/// <param name="Value">Pre-formatted value, for example <c>"3 h 12 min"</c>.</param>
/// <param name="Caption">Optional secondary line.</param>
/// <param name="Tone">Colour role of the value.</param>
/// <param name="Icon">Optional icon.</param>
public sealed record UiStatTile(string Label, string Value, string? Caption = null, UiTone Tone = UiTone.Neutral, UiIcon? Icon = null) : UiNode;

/// <summary>A horizontal progress bar.</summary>
/// <param name="Value">Current value, between 0 and <paramref name="Maximum"/>.</param>
/// <param name="Maximum">Maximum value; must be positive.</param>
/// <param name="Label">Optional label.</param>
/// <param name="Tone">Colour role of the bar.</param>
public sealed record UiProgress(double Value, double Maximum, string? Label = null, UiTone Tone = UiTone.Accent) : UiNode;

/// <summary>A labelled data point.</summary>
/// <param name="Label">Category or x-axis label.</param>
/// <param name="Value">Numeric value; must be finite.</param>
public sealed record UiDataPoint(string Label, double Value);

/// <summary>A named series for <see cref="UiLineChart"/>.</summary>
/// <param name="Name">Legend name.</param>
/// <param name="Points">Points in x order.</param>
public sealed record UiSeries(string Name, IReadOnlyList<UiDataPoint> Points);

/// <summary>A bar chart with one series.</summary>
/// <param name="Points">Bars in display order.</param>
/// <param name="Title">Optional title.</param>
/// <param name="ValueSuffix">Optional unit appended to values, for example <c>"min"</c>.</param>
public sealed record UiBarChart(IReadOnlyList<UiDataPoint> Points, string? Title = null, string? ValueSuffix = null) : UiNode;

/// <summary>A line chart with one or more series sharing the same x labels.</summary>
/// <param name="Series">Series to plot (at most 6).</param>
/// <param name="Title">Optional title.</param>
/// <param name="ValueSuffix">Optional unit appended to values.</param>
public sealed record UiLineChart(IReadOnlyList<UiSeries> Series, string? Title = null, string? ValueSuffix = null) : UiNode;

/// <summary>A donut chart: parts of a whole (at most 6 slices, values must be non-negative).</summary>
/// <param name="Slices">Slices in display order; each label is shown in the legend.</param>
/// <param name="Title">Optional title.</param>
/// <param name="CenterValue">Optional large text in the middle, for example a total.</param>
/// <param name="CenterLabel">Optional caption under <paramref name="CenterValue"/>.</param>
public sealed record UiDonutChart(IReadOnlyList<UiDataPoint> Slices, string? Title = null, string? CenterValue = null, string? CenterLabel = null) : UiNode;

/// <summary>
/// A heatmap grid: cell intensity is proportional to its value (for example activity by weekday and hour).
/// At most 12 rows and 24 columns; <paramref name="Values"/> has one row per row label and one value per column label.
/// </summary>
/// <param name="RowLabels">Row labels, top to bottom.</param>
/// <param name="ColumnLabels">Column labels, left to right (labels may be empty to keep the axis quiet).</param>
/// <param name="Values">Cell values (non-negative, finite), indexed [row][column].</param>
/// <param name="Title">Optional title.</param>
/// <param name="ValueSuffix">Optional unit shown in cell tooltips.</param>
public sealed record UiHeatmap(
    IReadOnlyList<string> RowLabels,
    IReadOnlyList<string> ColumnLabels,
    IReadOnlyList<IReadOnlyList<double>> Values,
    string? Title = null,
    string? ValueSuffix = null) : UiNode;

/// <summary>One day cell of a <see cref="UiMonthGrid"/>. Clicking it is delivered as <see cref="UiActionKind.Click"/> with <paramref name="Id"/>.</summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Day">Day of the month shown in the cell, 1-31.</param>
/// <param name="Markers">Number of entries on that day (0-99); the host draws up to three dots.</param>
/// <param name="IsToday">Whether the cell is today (drawn as a filled circle behind the number).</param>
/// <param name="IsSelected">Whether the cell is the selected day (drawn with a raised, outlined background).</param>
/// <param name="IsOutside">Whether the day belongs to the previous or next month (drawn dimmed).</param>
/// <param name="Tone">Colour role of the marker dots.</param>
/// <param name="Description">Optional text for the tooltip and screen readers, for example <c>"Thursday 19 November, 2 entries"</c>.</param>
/// <param name="Chips">Optional entry titles drawn as small labels inside the cell (full-size grids only, at most 4; the host shows
/// as many as fit and a "+N" overflow). When set, the host draws chips instead of marker dots.</param>
public sealed record UiMonthCell(
    string Id,
    int Day,
    int Markers = 0,
    bool IsToday = false,
    bool IsSelected = false,
    bool IsOutside = false,
    UiTone Tone = UiTone.Accent,
    string? Description = null,
    IReadOnlyList<string>? Chips = null);

/// <summary>
/// A month as a grid of seven equal columns: a weekday header row and one row per week, all in the same grid so the header
/// lines up with the days. The host draws the cells (today, selected, markers, dimmed days) and an optional header with the month
/// title and previous/next buttons.
/// </summary>
/// <param name="WeekdayLabels">Exactly seven column headings, first column first.</param>
/// <param name="Cells">Day cells, row by row: a multiple of seven, at most 42.</param>
/// <param name="Title">Optional month title shown above the grid.</param>
/// <param name="PreviousId">Optional id of the host-drawn "previous" button next to the title; set together with <paramref name="NextId"/>.</param>
/// <param name="NextId">Optional id of the host-drawn "next" button next to the title; set together with <paramref name="PreviousId"/>.</param>
/// <param name="IsCompact">Draw small cells, for dashboard widgets.</param>
public sealed record UiMonthGrid(
    IReadOnlyList<string> WeekdayLabels,
    IReadOnlyList<UiMonthCell> Cells,
    string? Title = null,
    string? PreviousId = null,
    string? NextId = null,
    bool IsCompact = false) : UiNode;

/// <summary>One column (day) of a <see cref="UiTimeGrid"/>. Clicking the column header is delivered as <see cref="UiActionKind.Click"/>
/// with <paramref name="Id"/> (for example to open that day).</summary>
/// <param name="Id">Element id of the header, unique within the tree.</param>
/// <param name="Label">Header text, for example <c>"Mon"</c>.</param>
/// <param name="Number">Optional prominent number under the label, for example the day of the month.</param>
/// <param name="IsToday">Whether the column is today (accent header, current-time line).</param>
public sealed record UiTimeGridDay(string Id, string Label, int? Number = null, bool IsToday = false);

/// <summary>
/// A timed block in a <see cref="UiTimeGrid"/>. Clicking it is delivered as <see cref="UiActionKind.Click"/> with <paramref name="Id"/>;
/// a Ctrl/Shift click carries <c>FormValues["extend"] = "true"</c> so the plugin can add it to a multi-selection. Dragging the body
/// (when <paramref name="IsMovable"/>) or the bottom edge (when <paramref name="IsResizable"/>) is delivered as
/// <see cref="UiActionKind.Schedule"/> with the new position.
/// </summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Day">Zero-based column index.</param>
/// <param name="StartMinute">Start, minutes after midnight (0-1439).</param>
/// <param name="EndMinute">End, minutes after midnight (StartMinute+1 to 1440). Blocks that cross midnight are split by the plugin.</param>
/// <param name="Title">Block title.</param>
/// <param name="Detail">Optional second line, for example <c>"09:00-10:30"</c>.</param>
/// <param name="Tone">Colour role.</param>
/// <param name="IsSelected">Whether the block is selected (outlined and raised).</param>
/// <param name="IsMovable">Whether the body can be dragged to another time or day.</param>
/// <param name="IsResizable">Whether the bottom edge can be dragged to change the end.</param>
public sealed record UiTimeGridEvent(
    string Id,
    int Day,
    int StartMinute,
    int EndMinute,
    string Title,
    string? Detail = null,
    UiTone Tone = UiTone.Accent,
    bool IsSelected = false,
    bool IsMovable = true,
    bool IsResizable = true)
{
    /// <summary>Gets whether the block lies in the past; the host draws it muted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPast { get; init; }
}

/// <summary>
/// A day or week schedule: one column per day, an hour ruler and timed blocks laid out side by side when they overlap. The host draws
/// everything, snaps drags to <paramref name="SnapMinutes"/> and shows a live preview while dragging; the plugin only receives the result.
/// <list type="bullet">
/// <item>Dragging across empty space in a column (or clicking it, which creates a one-hour block) is delivered as
/// <see cref="UiActionKind.Schedule"/> with <c>ElementId = Id</c>.</item>
/// <item>Moving or resizing a block is delivered as <see cref="UiActionKind.Schedule"/> with <c>ElementId</c> = the block id.</item>
/// <item>Schedule actions carry <c>FormValues</c> <c>day</c>, <c>start</c> and <c>end</c> (invariant integers, minutes after midnight).</item>
/// </list>
/// When <paramref name="Popover"/> is set the host shows it in a floating card next to the block <paramref name="PopoverAnchorId"/>
/// (or next to the pending new block when the anchor is <paramref name="Id"/>); clicking outside the card is delivered as a click on
/// <paramref name="DismissId"/>. Use it for the quick editor that appears only after the user picked something.
/// </summary>
/// <param name="Id">Element id of the grid, unique within the tree.</param>
/// <param name="Days">One to seven columns.</param>
/// <param name="Events">Timed blocks (at most the tree's item limit).</param>
/// <param name="SnapMinutes">Drag granularity: 5, 10, 15, 30 or 60.</param>
/// <param name="NowMinute">Optional current time, minutes after midnight; drawn as a line across the column marked today.</param>
/// <param name="ScrollToMinute">Time scrolled into view when the grid first appears (0-1439).</param>
/// <param name="AllowCreate">Whether drags on empty space create blocks.</param>
/// <param name="Popover">Optional floating content (no nested time grid).</param>
/// <param name="PopoverAnchorId">The block id, or <paramref name="Id"/> for the pending new block; required with <paramref name="Popover"/>.</param>
/// <param name="DismissId">Element id clicked when the popover is dismissed; required with <paramref name="Popover"/>.</param>
/// <param name="PendingDay">Column of the pending new block drawn as a ghost while its popover is open.</param>
/// <param name="PendingStartMinute">Start of the pending new block.</param>
/// <param name="PendingEndMinute">End of the pending new block.</param>
public sealed record UiTimeGrid(
    string Id,
    IReadOnlyList<UiTimeGridDay> Days,
    IReadOnlyList<UiTimeGridEvent> Events,
    int SnapMinutes = 15,
    int? NowMinute = null,
    int ScrollToMinute = 480,
    bool AllowCreate = true,
    UiNode? Popover = null,
    string? PopoverAnchorId = null,
    string? DismissId = null,
    int? PendingDay = null,
    int? PendingStartMinute = null,
    int? PendingEndMinute = null) : UiNode;

/// <summary>
/// A plugin's own vector logo, drawn in a host colour. <paramref name="PathData"/> uses SVG/WPF path syntax limited to
/// the commands M L H V C S Q T A Z (upper or lower case), numbers, spaces and commas, at most 4096 characters.
/// The path is scaled uniformly to <paramref name="Size"/>.
/// </summary>
/// <param name="PathData">Path geometry.</param>
/// <param name="Size">Edge length in device-independent pixels (12-96).</param>
/// <param name="Tone">Host colour role.</param>
public sealed record UiGlyph(string PathData, double Size = 32, UiTone Tone = UiTone.Accent) : UiNode;

/// <summary>A table column definition.</summary>
/// <param name="Header">Column header.</param>
/// <param name="Alignment">Cell alignment.</param>
/// <param name="Monospace">Render cells in the monospace font.</param>
public sealed record UiTableColumn(string Header, UiAlignment Alignment = UiAlignment.Start, bool Monospace = false);

/// <summary>A table row; must have exactly one cell per column.</summary>
/// <param name="Cells">Cell texts.</param>
public sealed record UiTableRow(IReadOnlyList<string> Cells);

/// <summary>A read-only data table.</summary>
/// <param name="Columns">Column definitions (1-12).</param>
/// <param name="Rows">Rows.</param>
public sealed record UiTable(IReadOnlyList<UiTableColumn> Columns, IReadOnlyList<UiTableRow> Rows) : UiNode;

/// <summary>A button. Clicks are delivered to the plugin as <see cref="UiActionKind.Click"/>.</summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Label">Button text.</param>
/// <param name="Variant">Visual weight.</param>
/// <param name="Icon">Optional icon.</param>
/// <param name="IsEnabled">Whether the button can be clicked.</param>
/// <param name="ShortLabel">Optional compact text drawn instead of <paramref name="Label"/> (for example a minus sign); the label stays the accessible name and tooltip.</param>
public sealed record UiButton(string Id, string Label, UiButtonVariant Variant = UiButtonVariant.Secondary, UiIcon? Icon = null, bool IsEnabled = true, string? ShortLabel = null) : UiNode
{
    /// <summary>Gets whether only the icon is drawn (the label stays the tooltip and accessible name); requires <see cref="Icon"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IconOnly { get; init; }
}

/// <summary>An on/off switch, or a tick box in list rows. Changes are delivered as <see cref="UiActionKind.Toggle"/> with value <c>"true"</c>/<c>"false"</c>.</summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Label">Label.</param>
/// <param name="IsOn">Current state.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record UiToggle(string Id, string Label, bool IsOn, string? Description = null) : UiNode
{
    /// <summary>Gets how the toggle is drawn; the state and the delivered action are the same in every style.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public UiToggleStyle Style { get; init; }
}

/// <summary>A form. Submission is delivered as <see cref="UiActionKind.Submit"/> with all field values.</summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Fields">Input fields.</param>
/// <param name="SubmitLabel">Text of the submit button.</param>
public sealed record UiForm(string Id, IReadOnlyList<UiFormField> Fields, string SubmitLabel) : UiNode
{
    /// <summary>Gets an optional text-link button drawn left of the submit button (in a floating card, in the pinned footer).</summary>
    public UiButton? FooterAction { get; init; }
}

/// <summary>Base of form input fields. Field ids must be unique within the whole tree.</summary>
/// <param name="Id">Field id; used as the key in <see cref="UiAction.FormValues"/>.</param>
/// <param name="Label">Field label.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(UiTextField), "text")]
[JsonDerivedType(typeof(UiNumberField), "number")]
[JsonDerivedType(typeof(UiSelectField), "select")]
[JsonDerivedType(typeof(UiCheckboxField), "checkbox")]
[JsonDerivedType(typeof(UiNoteField), "note")]
public abstract record UiFormField(string Id, string Label)
{
    /// <summary>Gets how the field is drawn; <see cref="UiFieldStyle.Standard"/> is a labelled field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public UiFieldStyle Style { get; init; }
}

/// <summary>Single-line text input.</summary>
/// <param name="Id">Field id.</param>
/// <param name="Label">Label.</param>
/// <param name="Value">Initial value.</param>
/// <param name="Placeholder">Placeholder text.</param>
/// <param name="MaxLength">Maximum length (1-2000).</param>
public sealed record UiTextField(string Id, string Label, string Value = "", string? Placeholder = null, int MaxLength = 200) : UiFormField(Id, Label);

/// <summary>Numeric input with bounds.</summary>
/// <param name="Id">Field id.</param>
/// <param name="Label">Label.</param>
/// <param name="Value">Initial value.</param>
/// <param name="Minimum">Lower bound.</param>
/// <param name="Maximum">Upper bound.</param>
/// <param name="Step">Increment.</param>
public sealed record UiNumberField(string Id, string Label, double Value, double Minimum, double Maximum, double Step = 1) : UiFormField(Id, Label);

/// <summary>A selectable option.</summary>
/// <param name="Value">Value reported on submit.</param>
/// <param name="Label">Displayed text.</param>
public sealed record UiOption(string Value, string Label);

/// <summary>Drop-down selection.</summary>
/// <param name="Id">Field id.</param>
/// <param name="Label">Label.</param>
/// <param name="Options">Available options (1-100).</param>
/// <param name="SelectedValue">Initially selected value, or <see langword="null"/>.</param>
public sealed record UiSelectField(string Id, string Label, IReadOnlyList<UiOption> Options, string? SelectedValue = null) : UiFormField(Id, Label);

/// <summary>Checkbox; reported as <c>"true"</c>/<c>"false"</c>.</summary>
/// <param name="Id">Field id.</param>
/// <param name="Label">Label.</param>
/// <param name="IsChecked">Initial state.</param>
public sealed record UiCheckboxField(string Id, string Label, bool IsChecked = false) : UiFormField(Id, Label);

/// <summary>Read-only line inside a form (for example a summary of the chosen date). It is not a value: the form does not submit it.</summary>
/// <param name="Id">Field id.</param>
/// <param name="Label">Accessible name.</param>
/// <param name="Text">Main line.</param>
/// <param name="Detail">Optional muted line below.</param>
/// <param name="Icon">Optional icon before the text.</param>
public sealed record UiNoteField(string Id, string Label, string Text, string? Detail = null, UiIcon? Icon = null) : UiFormField(Id, Label);
