using Blocker.UI.Sdk;

namespace Blocker.SDK.Tests;

public class UiTreeValidatorTests
{
    private static readonly UiLimits SmallText = new(MaxDepth: 16, MaxNodes: 2000, MaxTextLength: 5, MaxItems: 500);

    private static UiPage In(params UiNode[] children) => Ui.Page("T", children);

    private static void AssertSingleError(UiNode? tree, string expected, UiLimits? limits = null)
    {
        UiValidationResult result = UiTreeValidator.Validate(tree, limits);

        Assert.False(result.IsValid);
        Assert.Equal([expected], result.Errors);
    }

    [Fact]
    public void Validate_TreeBuiltWithFactory_IsValid()
    {
        UiPage tree = Ui.Page(
            "Focus",
            "Today",
            Ui.Grid(2, Ui.Stat("Blocked", "12", "today", UiTone.Accent, UiIcon.Shield), Ui.Progress(3, 10, "Goal")),
            Ui.Card("Chart", Ui.BarChart("Apps", Ui.Point("a", 1), Ui.Point("b", 2.5))),
            Ui.Row(Ui.Button("go", "Go", UiButtonVariant.Primary), Ui.Toggle("t", "On", true, "desc")),
            Ui.Form("f", "Save", new UiTextField("name", "Name", "x"), new UiNumberField("n", "N", 5, 0, 10)),
            Ui.Text("line1\nline2\tTabbed"));

        Assert.Empty(UiTreeValidator.Validate(tree).Errors);
    }

    [Fact]
    public void Validate_NullRoot_ReportsError() => AssertSingleError(null, "The UI tree is null.");

    [Fact]
    public void Validate_PageNotRoot_ReportsError() =>
        AssertSingleError(In(Ui.Column(Ui.Page("Inner"))), "UiPage is only allowed as the root node.");

    [Fact]
    public void Validate_DuplicateIds_ReportsError() =>
        AssertSingleError(In(Ui.Button("a", "A"), Ui.Toggle("a", "B", false)), "Element id 'a' is used more than once.");

    [Theory]
    [InlineData("1bad")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("ünï")]
    public void Validate_InvalidId_ReportsError(string id) =>
        AssertSingleError(
            In(Ui.Button(id, "A")),
            $"Element id '{id}' is invalid (letter first, then letters, digits, '_' or '-', max 64).");

    [Fact]
    public void Validate_IdOf64Chars_IsValid() =>
        Assert.True(UiTreeValidator.Validate(In(Ui.Button("a" + new string('b', 63), "A"))).IsValid);

    [Fact]
    public void Validate_IdOf65Chars_ReportsError() =>
        Assert.False(UiTreeValidator.Validate(In(Ui.Button("a" + new string('b', 64), "A"))).IsValid);

    [Fact]
    public void Validate_DepthAboveMax_ReportsError() =>
        AssertSingleError(
            Ui.Page("T", Ui.Column(Ui.Column(Ui.Text("x")))),
            "Tree exceeds the maximum depth of 2.",
            new UiLimits(2, 2000, 2000, 500));

    [Fact]
    public void Validate_DepthEqualToMax_IsValid() =>
        Assert.True(UiTreeValidator.Validate(Ui.Page("T", Ui.Column(Ui.Text("x"))), new UiLimits(3, 2000, 2000, 500)).IsValid);

    [Fact]
    public void Validate_NodeCountAboveMax_ReportsErrorOnce() =>
        AssertSingleError(
            In(Ui.Text("1"), Ui.Text("2"), Ui.Text("3"), Ui.Text("4"), Ui.Text("5")),
            "Tree exceeds the maximum of 3 nodes.",
            new UiLimits(16, 3, 2000, 500));

    [Fact]
    public void Validate_TextWithControlCharacter_ReportsError() =>
        AssertSingleError(In(Ui.Text("a\u0001b")), "UiText.Text contains control characters.");

    [Fact]
    public void Validate_TextTooLong_ReportsError() =>
        AssertSingleError(In(Ui.Text("abcdef")), "UiText.Text exceeds 5 characters.", SmallText);

    [Fact]
    public void Validate_TextAtMaxLength_IsValid() =>
        Assert.True(UiTreeValidator.Validate(In(Ui.Text("abcde")), SmallText).IsValid);

    [Fact]
    public void Validate_BlankRequiredText_ReportsError() =>
        AssertSingleError(In(Ui.Text("  ")), "UiText.Text is required.");

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void Validate_GridColumnsOutOfRange_ReportsError(int columns) =>
        AssertSingleError(In(Ui.Grid(columns)), "UiGrid.Columns must be between 1 and 6.");

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Validate_GridColumnsInRange_IsValid(int columns) =>
        Assert.True(UiTreeValidator.Validate(In(Ui.Grid(columns))).IsValid);

    [Theory]
    [InlineData(11, 10, "UiProgress.Value must be between 0 and Maximum.")]
    [InlineData(-1, 10, "UiProgress.Value must be between 0 and Maximum.")]
    [InlineData(double.NaN, 10, "UiProgress.Value must be between 0 and Maximum.")]
    [InlineData(0, 0, "UiProgress.Maximum must be a positive finite number.")]
    [InlineData(1, double.PositiveInfinity, "UiProgress.Maximum must be a positive finite number.")]
    public void Validate_ProgressOutOfRange_ReportsError(double value, double maximum, string expected) =>
        AssertSingleError(In(Ui.Progress(value, maximum)), expected);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 10)]
    public void Validate_ProgressBoundaries_IsValid(double value, double maximum) =>
        Assert.True(UiTreeValidator.Validate(In(Ui.Progress(value, maximum))).IsValid);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_ChartNonFiniteValue_ReportsError(double value) =>
        AssertSingleError(In(Ui.BarChart("C", Ui.Point("a", value))), "UiBarChart.Points contains a non-finite value.");

    [Fact]
    public void Validate_LineChartWithoutSeries_ReportsError() =>
        AssertSingleError(In(new UiLineChart([])), "UiLineChart.Series must contain 1-6 series.");

    [Fact]
    public void Validate_TableRowCellMismatch_ReportsError() =>
        AssertSingleError(
            In(new UiTable([new UiTableColumn("A"), new UiTableColumn("B")], [new UiTableRow(["only"])])),
            "Every UiTableRow must have exactly one cell per column.");

    [Fact]
    public void Validate_TableMatchingRows_IsValid() =>
        Assert.True(UiTreeValidator.Validate(
            In(new UiTable([new UiTableColumn("A"), new UiTableColumn("B")], [new UiTableRow(["1", "2"])]))).IsValid);

    [Theory]
    [InlineData(11, 0, 10, 1)]
    [InlineData(-1, 0, 10, 1)]
    [InlineData(5, 10, 0, 1)]
    [InlineData(5, 0, 10, 0)]
    [InlineData(double.NaN, 0, 10, 1)]
    public void Validate_NumberFieldInvalidBounds_ReportsError(double value, double min, double max, double step) =>
        AssertSingleError(
            In(Ui.Form("f", "Save", new UiNumberField("n", "N", value, min, max, step))),
            "UiNumberField requires finite Minimum <= Value <= Maximum and a positive Step.");

    [Fact]
    public void Validate_SelectedValueNotInOptions_ReportsError() =>
        AssertSingleError(
            In(Ui.Form("f", "Save", new UiSelectField("s", "S", [new UiOption("a", "A")], "zzz"))),
            "UiSelectField.SelectedValue must match one of the options.");

    [Fact]
    public void Validate_SelectedValueInOptions_IsValid() =>
        Assert.True(UiTreeValidator.Validate(
            In(Ui.Form("f", "Save", new UiSelectField("s", "S", [new UiOption("a", "A")], "a")))).IsValid);

    [Fact]
    public void Validate_FormWithoutFields_ReportsError() =>
        AssertSingleError(In(Ui.Form("f", "Save")), "UiForm.Fields must contain 1-500 fields.");

    [Fact]
    public void Validate_TextFieldValueLongerThanMaxLength_ReportsError() =>
        AssertSingleError(
            In(Ui.Form("f", "Save", new UiTextField("t", "T", "abcd", MaxLength: 3))),
            "UiTextField.Value is longer than MaxLength.");

    [Fact]
    public void Validate_UndefinedTone_ReportsError() =>
        AssertSingleError(In(new UiText("x", Tone: (UiTone)99)), "UiText.Tone has an undefined value.");

    [Fact]
    public void Validate_UndefinedIcon_ReportsError() =>
        AssertSingleError(In(Ui.Stat("L", "V", icon: (UiIcon)999)), "UiStatTile.Icon is not a defined UiIcon.");

    [Fact]
    public void Validate_NullChild_ReportsError() =>
        AssertSingleError(new UiPage("T", null, [null!]), "A child node is null.");

    [Fact]
    public void Validate_NullChildrenCollection_ReportsError() =>
        AssertSingleError(new UiPage("T", null, null!), "Children collection is null.");

    [Fact]
    public void Validate_MultipleProblems_ReportsEachError()
    {
        UiValidationResult result = UiTreeValidator.Validate(In(Ui.Grid(0), Ui.Progress(5, 1)));

        Assert.Equal(
            ["UiGrid.Columns must be between 1 and 6.", "UiProgress.Value must be between 0 and Maximum."],
            result.Errors);
    }

    [Theory]
    [InlineData("Home")]
    [InlineData("A")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("Tab\there")]
    public void ValidateNavigation_ValidTitle_IsValid(string title) =>
        Assert.True(UiTreeValidator.ValidateNavigation(new UiNavigationItem(title, UiIcon.Clock)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData("bad\u0001title")]
    public void ValidateNavigation_InvalidTitle_ReportsError(string title)
    {
        UiValidationResult result = UiTreeValidator.ValidateNavigation(new UiNavigationItem(title, UiIcon.Clock));

        Assert.Equal(["Navigation title must be 1-32 printable characters."], result.Errors);
    }

    [Fact]
    public void ValidateNavigation_UndefinedIcon_ReportsError()
    {
        UiValidationResult result = UiTreeValidator.ValidateNavigation(new UiNavigationItem("Home", (UiIcon)999));

        Assert.Equal(["Navigation icon is not a defined UiIcon."], result.Errors);
    }

    [Fact]
    public void ValidateNavigation_Null_ReportsError() =>
        Assert.Equal(["Navigation item is null."], UiTreeValidator.ValidateNavigation(null).Errors);
}
