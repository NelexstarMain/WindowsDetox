using System.Text.Json.Serialization;

namespace Blocker.UI.Sdk;

/// <summary>
/// Base of the declarative UI tree. A tree is plain, serializable data: plugins describe <em>what</em> to show
/// and the host renders it with its own controls, palette, blueprint grid and metaball layer. Plugins cannot
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
public sealed record UiButton(string Id, string Label, UiButtonVariant Variant = UiButtonVariant.Secondary, UiIcon? Icon = null, bool IsEnabled = true) : UiNode;

/// <summary>An on/off switch. Changes are delivered as <see cref="UiActionKind.Toggle"/> with value <c>"true"</c>/<c>"false"</c>.</summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Label">Label.</param>
/// <param name="IsOn">Current state.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record UiToggle(string Id, string Label, bool IsOn, string? Description = null) : UiNode;

/// <summary>A form. Submission is delivered as <see cref="UiActionKind.Submit"/> with all field values.</summary>
/// <param name="Id">Element id, unique within the tree.</param>
/// <param name="Fields">Input fields.</param>
/// <param name="SubmitLabel">Text of the submit button.</param>
public sealed record UiForm(string Id, IReadOnlyList<UiFormField> Fields, string SubmitLabel) : UiNode;

/// <summary>Base of form input fields. Field ids must be unique within the whole tree.</summary>
/// <param name="Id">Field id; used as the key in <see cref="UiAction.FormValues"/>.</param>
/// <param name="Label">Field label.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(UiTextField), "text")]
[JsonDerivedType(typeof(UiNumberField), "number")]
[JsonDerivedType(typeof(UiSelectField), "select")]
[JsonDerivedType(typeof(UiCheckboxField), "checkbox")]
public abstract record UiFormField(string Id, string Label);

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
