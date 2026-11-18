using System.Text.RegularExpressions;

namespace Blocker.UI.Sdk;

/// <summary>Limits enforced on every UI tree received from a plugin.</summary>
/// <param name="MaxDepth">Maximum nesting depth.</param>
/// <param name="MaxNodes">Maximum number of nodes (including form fields, data points and table rows).</param>
/// <param name="MaxTextLength">Maximum length of any string.</param>
/// <param name="MaxItems">Maximum number of chart points, table rows or select options in one element.</param>
public sealed record UiLimits(int MaxDepth, int MaxNodes, int MaxTextLength, int MaxItems)
{
    /// <summary>Gets the default limits used by the host.</summary>
    public static UiLimits Default { get; } = new(MaxDepth: 16, MaxNodes: 2000, MaxTextLength: 2000, MaxItems: 500);
}

/// <summary>Result of <see cref="UiTreeValidator.Validate"/>.</summary>
/// <param name="Errors">Human-readable problems; empty when the tree is valid.</param>
public sealed record UiValidationResult(IReadOnlyList<string> Errors)
{
    /// <summary>Gets whether the tree passed validation.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates plugin-supplied UI trees before rendering. The host rejects (and does not render) any invalid
/// tree, so a plugin cannot exhaust UI resources or inject control characters into the interface.
/// </summary>
public static partial class UiTreeValidator
{
    private const int MaxSeries = 6;
    private const int MaxHeatmapRows = 12;
    private const int MaxHeatmapColumns = 24;
    private const int MaxGlyphPathLength = 4096;
    private const int MaxColumns = 12;
    private const int MaxGridColumns = 6;
    private const int MaxNavigationTitle = 32;

    /// <summary>Checks SVG path data as accepted for <see cref="UiGlyph"/> and plugin icons: 1-4096 characters of path commands, numbers, spaces and commas.</summary>
    /// <param name="pathData">Path data.</param>
    /// <returns><see langword="true"/> when the data is acceptable.</returns>
    public static bool IsValidPathData(string? pathData) =>
        !string.IsNullOrWhiteSpace(pathData) && pathData.Length <= MaxGlyphPathLength && pathData.All(IsGlyphPathChar);

    private static bool IsGlyphPathChar(char c) =>
        char.IsAsciiDigit(c) || c is ' ' or ',' or '.' or '-' or '+' or 'e' or 'E'
        || "MmLlHhVvCcSsQqTtAaZz".Contains(c, StringComparison.Ordinal);

    /// <summary>Validates <paramref name="root"/> against <paramref name="limits"/>.</summary>
    /// <param name="root">Root node.</param>
    /// <param name="limits">Limits; <see langword="null"/> uses <see cref="UiLimits.Default"/>.</param>
    /// <returns>The validation result.</returns>
    public static UiValidationResult Validate(UiNode? root, UiLimits? limits = null)
    {
        var walker = new Walker(limits ?? UiLimits.Default);
        if (root is null)
        {
            walker.Errors.Add("The UI tree is null.");
        }
        else
        {
            walker.Visit(root, depth: 1, isRoot: true);
        }

        return new UiValidationResult(walker.Errors);
    }

    /// <summary>Validates a navigation entry.</summary>
    /// <param name="item">Entry to validate.</param>
    /// <returns>The validation result.</returns>
    public static UiValidationResult ValidateNavigation(UiNavigationItem? item)
    {
        List<string> errors = [];
        if (item is null)
        {
            errors.Add("Navigation item is null.");
        }
        else if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > MaxNavigationTitle || ContainsControlCharacters(item.Title))
        {
            errors.Add($"Navigation title must be 1-{MaxNavigationTitle} printable characters.");
        }
        else if (!Enum.IsDefined(item.Icon))
        {
            errors.Add("Navigation icon is not a defined UiIcon.");
        }

        return new UiValidationResult(errors);
    }

    private static bool ContainsControlCharacters(string text)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c) && c is not '\n' and not '\t')
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ElementIdPattern();

    private sealed class Walker(UiLimits limits)
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private int _nodeCount;

        public List<string> Errors { get; } = [];

        public void Visit(UiNode node, int depth, bool isRoot)
        {
            if (node is null)
            {
                Errors.Add("A child node is null.");
                return;
            }

            if (depth > limits.MaxDepth)
            {
                Errors.Add($"Tree exceeds the maximum depth of {limits.MaxDepth}.");
                return;
            }

            if (!Count(1))
            {
                return;
            }

            switch (node)
            {
                case UiPage page:
                    if (!isRoot)
                    {
                        Errors.Add("UiPage is only allowed as the root node.");
                    }

                    RequiredText(page.Title, "UiPage.Title");
                    OptionalText(page.Subtitle, "UiPage.Subtitle");
                    Children(page.Children, depth);
                    break;
                case UiStack stack:
                    Defined(stack.Orientation, "UiStack.Orientation");
                    Defined(stack.Gap, "UiStack.Gap");
                    Children(stack.Children, depth);
                    break;
                case UiGrid grid:
                    if (grid.Columns is < 1 or > MaxGridColumns)
                    {
                        Errors.Add($"UiGrid.Columns must be between 1 and {MaxGridColumns}.");
                    }

                    Children(grid.Children, depth);
                    break;
                case UiCard card:
                    OptionalText(card.Title, "UiCard.Title");
                    OptionalText(card.Subtitle, "UiCard.Subtitle");
                    OptionalIcon(card.Icon, "UiCard.Icon");
                    Children(card.Children, depth);
                    break;
                case UiText text:
                    RequiredText(text.Text, "UiText.Text");
                    Defined(text.Style, "UiText.Style");
                    Defined(text.Tone, "UiText.Tone");
                    break;
                case UiStatTile stat:
                    RequiredText(stat.Label, "UiStatTile.Label");
                    RequiredText(stat.Value, "UiStatTile.Value");
                    OptionalText(stat.Caption, "UiStatTile.Caption");
                    Defined(stat.Tone, "UiStatTile.Tone");
                    OptionalIcon(stat.Icon, "UiStatTile.Icon");
                    break;
                case UiProgress progress:
                    if (!double.IsFinite(progress.Maximum) || progress.Maximum <= 0)
                    {
                        Errors.Add("UiProgress.Maximum must be a positive finite number.");
                    }
                    else if (!double.IsFinite(progress.Value) || progress.Value < 0 || progress.Value > progress.Maximum)
                    {
                        Errors.Add("UiProgress.Value must be between 0 and Maximum.");
                    }

                    OptionalText(progress.Label, "UiProgress.Label");
                    Defined(progress.Tone, "UiProgress.Tone");
                    break;
                case UiBarChart bar:
                    OptionalText(bar.Title, "UiBarChart.Title");
                    OptionalText(bar.ValueSuffix, "UiBarChart.ValueSuffix");
                    Points(bar.Points, "UiBarChart.Points");
                    break;
                case UiLineChart line:
                    OptionalText(line.Title, "UiLineChart.Title");
                    OptionalText(line.ValueSuffix, "UiLineChart.ValueSuffix");
                    if (line.Series is null || line.Series.Count is 0 or > MaxSeries)
                    {
                        Errors.Add($"UiLineChart.Series must contain 1-{MaxSeries} series.");
                        break;
                    }

                    foreach (UiSeries series in line.Series)
                    {
                        if (series is null)
                        {
                            Errors.Add("UiLineChart.Series contains null.");
                            continue;
                        }

                        RequiredText(series.Name, "UiSeries.Name");
                        Points(series.Points, "UiSeries.Points");
                    }

                    break;
                case UiDonutChart donut:
                    OptionalText(donut.Title, "UiDonutChart.Title");
                    OptionalText(donut.CenterValue, "UiDonutChart.CenterValue");
                    OptionalText(donut.CenterLabel, "UiDonutChart.CenterLabel");
                    Points(donut.Slices, "UiDonutChart.Slices");
                    if (donut.Slices is { Count: > MaxSeries })
                    {
                        Errors.Add($"UiDonutChart.Slices must contain at most {MaxSeries} slices.");
                    }
                    else if (donut.Slices is not null && donut.Slices.Any(p => p is not null && p.Value < 0))
                    {
                        Errors.Add("UiDonutChart.Slices values must be non-negative.");
                    }

                    break;
                case UiHeatmap heatmap:
                    Heatmap(heatmap);
                    break;
                case UiGlyph glyph:
                    if (!IsValidPathData(glyph.PathData))
                    {
                        Errors.Add($"UiGlyph.PathData must be 1-{MaxGlyphPathLength} characters of path commands (MLHVCSQTAZ), numbers, spaces and commas.");
                    }

                    if (!double.IsFinite(glyph.Size) || glyph.Size is < 12 or > 96)
                    {
                        Errors.Add("UiGlyph.Size must be between 12 and 96.");
                    }

                    Defined(glyph.Tone, "UiGlyph.Tone");
                    break;
                case UiTable table:
                    Table(table);
                    break;
                case UiButton button:
                    Id(button.Id);
                    RequiredText(button.Label, "UiButton.Label");
                    Defined(button.Variant, "UiButton.Variant");
                    OptionalIcon(button.Icon, "UiButton.Icon");
                    break;
                case UiToggle toggle:
                    Id(toggle.Id);
                    RequiredText(toggle.Label, "UiToggle.Label");
                    OptionalText(toggle.Description, "UiToggle.Description");
                    break;
                case UiForm form:
                    Form(form);
                    break;
                default:
                    Errors.Add($"Unsupported node type '{node.GetType().Name}'.");
                    break;
            }
        }

        private bool Count(int amount)
        {
            _nodeCount += amount;
            if (_nodeCount > limits.MaxNodes)
            {
                if (_nodeCount - amount <= limits.MaxNodes)
                {
                    Errors.Add($"Tree exceeds the maximum of {limits.MaxNodes} nodes.");
                }

                return false;
            }

            return true;
        }

        private void Children(IReadOnlyList<UiNode>? children, int depth)
        {
            if (children is null)
            {
                Errors.Add("Children collection is null.");
                return;
            }

            foreach (UiNode child in children)
            {
                Visit(child, depth + 1, isRoot: false);
            }
        }

        private void Heatmap(UiHeatmap heatmap)
        {
            OptionalText(heatmap.Title, "UiHeatmap.Title");
            OptionalText(heatmap.ValueSuffix, "UiHeatmap.ValueSuffix");
            if (heatmap.RowLabels is null || heatmap.RowLabels.Count is 0 or > MaxHeatmapRows
                || heatmap.ColumnLabels is null || heatmap.ColumnLabels.Count is 0 or > MaxHeatmapColumns)
            {
                Errors.Add($"UiHeatmap needs 1-{MaxHeatmapRows} row labels and 1-{MaxHeatmapColumns} column labels.");
                return;
            }

            foreach (string label in heatmap.RowLabels.Concat(heatmap.ColumnLabels))
            {
                if (label is null)
                {
                    Errors.Add("UiHeatmap labels must not be null.");
                    return;
                }

                OptionalText(label, "UiHeatmap label");
            }

            if (heatmap.Values is null || heatmap.Values.Count != heatmap.RowLabels.Count
                || heatmap.Values.Any(row => row is null || row.Count != heatmap.ColumnLabels.Count))
            {
                Errors.Add("UiHeatmap.Values must have one row per row label and one value per column label.");
                return;
            }

            if (heatmap.Values.Any(row => row.Any(v => !double.IsFinite(v) || v < 0)))
            {
                Errors.Add("UiHeatmap.Values must be finite and non-negative.");
            }
        }

        private void Points(IReadOnlyList<UiDataPoint>? points, string name)
        {
            if (points is null || points.Count > limits.MaxItems)
            {
                Errors.Add($"{name} must contain at most {limits.MaxItems} points.");
                return;
            }

            if (!Count(points.Count))
            {
                return;
            }

            foreach (UiDataPoint point in points)
            {
                if (point is null)
                {
                    Errors.Add($"{name} contains null.");
                    continue;
                }

                RequiredText(point.Label, $"{name}.Label");
                if (!double.IsFinite(point.Value))
                {
                    Errors.Add($"{name} contains a non-finite value.");
                }
            }
        }

        private void Table(UiTable table)
        {
            if (table.Columns is null || table.Columns.Count is 0 or > MaxColumns)
            {
                Errors.Add($"UiTable.Columns must contain 1-{MaxColumns} columns.");
                return;
            }

            foreach (UiTableColumn column in table.Columns)
            {
                if (column is null)
                {
                    Errors.Add("UiTable.Columns contains null.");
                    return;
                }

                RequiredText(column.Header, "UiTableColumn.Header");
                Defined(column.Alignment, "UiTableColumn.Alignment");
            }

            if (table.Rows is null || table.Rows.Count > limits.MaxItems)
            {
                Errors.Add($"UiTable.Rows must contain at most {limits.MaxItems} rows.");
                return;
            }

            if (!Count(table.Rows.Count))
            {
                return;
            }

            foreach (UiTableRow row in table.Rows)
            {
                if (row?.Cells is null || row.Cells.Count != table.Columns.Count)
                {
                    Errors.Add("Every UiTableRow must have exactly one cell per column.");
                    return;
                }

                foreach (string cell in row.Cells)
                {
                    OptionalText(cell, "UiTableRow.Cells");
                }
            }
        }

        private void Form(UiForm form)
        {
            Id(form.Id);
            RequiredText(form.SubmitLabel, "UiForm.SubmitLabel");
            if (form.Fields is null || form.Fields.Count is 0 || form.Fields.Count > limits.MaxItems)
            {
                Errors.Add($"UiForm.Fields must contain 1-{limits.MaxItems} fields.");
                return;
            }

            if (!Count(form.Fields.Count))
            {
                return;
            }

            foreach (UiFormField field in form.Fields)
            {
                if (field is null)
                {
                    Errors.Add("UiForm.Fields contains null.");
                    continue;
                }

                Id(field.Id);
                RequiredText(field.Label, "UiFormField.Label");
                switch (field)
                {
                    case UiTextField text:
                        if (text.MaxLength is < 1 || text.MaxLength > limits.MaxTextLength)
                        {
                            Errors.Add($"UiTextField.MaxLength must be between 1 and {limits.MaxTextLength}.");
                        }

                        OptionalText(text.Value, "UiTextField.Value");
                        OptionalText(text.Placeholder, "UiTextField.Placeholder");
                        if (text.Value is not null && text.Value.Length > text.MaxLength)
                        {
                            Errors.Add("UiTextField.Value is longer than MaxLength.");
                        }

                        break;
                    case UiNumberField number:
                        if (!double.IsFinite(number.Minimum) || !double.IsFinite(number.Maximum) || !double.IsFinite(number.Step)
                            || number.Minimum > number.Maximum || number.Step <= 0
                            || !double.IsFinite(number.Value) || number.Value < number.Minimum || number.Value > number.Maximum)
                        {
                            Errors.Add("UiNumberField requires finite Minimum <= Value <= Maximum and a positive Step.");
                        }

                        break;
                    case UiSelectField select:
                        if (select.Options is null || select.Options.Count is 0 || select.Options.Count > limits.MaxItems)
                        {
                            Errors.Add($"UiSelectField.Options must contain 1-{limits.MaxItems} options.");
                            break;
                        }

                        foreach (UiOption option in select.Options)
                        {
                            if (option is null)
                            {
                                Errors.Add("UiSelectField.Options contains null.");
                                continue;
                            }

                            RequiredText(option.Value, "UiOption.Value");
                            RequiredText(option.Label, "UiOption.Label");
                        }

                        if (select.SelectedValue is not null && !select.Options.Any(o => o is not null && string.Equals(o.Value, select.SelectedValue, StringComparison.Ordinal)))
                        {
                            Errors.Add("UiSelectField.SelectedValue must match one of the options.");
                        }

                        break;
                    case UiCheckboxField:
                        break;
                    default:
                        Errors.Add($"Unsupported form field type '{field.GetType().Name}'.");
                        break;
                }
            }
        }

        private void Id(string? id)
        {
            if (id is null || !ElementIdPattern().IsMatch(id))
            {
                Errors.Add($"Element id '{id}' is invalid (letter first, then letters, digits, '_' or '-', max 64).");
            }
            else if (!_ids.Add(id))
            {
                Errors.Add($"Element id '{id}' is used more than once.");
            }
        }

        private void RequiredText(string? text, string name)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Errors.Add($"{name} is required.");
                return;
            }

            OptionalText(text, name);
        }

        private void OptionalText(string? text, string name)
        {
            if (text is null)
            {
                return;
            }

            if (text.Length > limits.MaxTextLength)
            {
                Errors.Add($"{name} exceeds {limits.MaxTextLength} characters.");
            }
            else if (ContainsControlCharacters(text))
            {
                Errors.Add($"{name} contains control characters.");
            }
        }

        private void OptionalIcon(UiIcon? icon, string name)
        {
            if (icon is { } value && !Enum.IsDefined(value))
            {
                Errors.Add($"{name} is not a defined UiIcon.");
            }
        }

        private void Defined<TEnum>(TEnum value, string name)
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                Errors.Add($"{name} has an undefined value.");
            }
        }
    }
}
