using Blocker.UI.Sdk;

namespace Blocker.SDK.Tests;

public class UiTimeGridValidatorTests
{
    private static readonly UiTimeGridDay[] Week =
        [.. Enumerable.Range(0, 7).Select(i => new UiTimeGridDay("day" + i, "D" + i, i + 1, IsToday: i == 2))];

    private static UiPage In(UiNode node) => Ui.Page("T", [node]);

    private static IReadOnlyList<string> Errors(UiNode node) => UiTreeValidator.Validate(In(node)).Errors;

    private static UiTimeGrid Grid(params UiTimeGridEvent[] events) => Ui.TimeGrid("grid", Week, events, nowMinute: 630);

    [Fact]
    public void Validate_WeekWithEventsAndPopover_IsValidAndSurvivesJson()
    {
        UiTimeGrid grid = Grid(new UiTimeGridEvent("e1", 2, 540, 630, "Deep work", "09:00-10:30", IsSelected: true)) with
        {
            Popover = Ui.Column(Ui.Text("Deep work"), Ui.Button("delete", "Delete")),
            PopoverAnchorId = "e1",
            DismissId = "dismiss",
        };

        Assert.Empty(Errors(grid));

        UiTimeGrid round = Assert.IsType<UiTimeGrid>(UiJson.Deserialize(UiJson.Serialize(grid)));
        Assert.Equal(grid.Events, round.Events);
        Assert.Equal(grid.Days, round.Days);
        Assert.Equal("e1", round.PopoverAnchorId);
        Assert.IsType<UiStack>(round.Popover);
    }

    [Fact]
    public void Validate_PendingBlockAnchoredToGrid_IsValid() =>
        Assert.Empty(Errors(Grid() with
        {
            Popover = Ui.Text("New"),
            PopoverAnchorId = "grid",
            DismissId = "dismiss",
            PendingDay = 1,
            PendingStartMinute = 600,
            PendingEndMinute = 660,
        }));

    [Theory]
    [InlineData(0, 60, 60)]
    [InlineData(0, 1380, 1441)]
    [InlineData(-1, 0, 60)]
    [InlineData(7, 0, 60)]
    public void Validate_EventOutsideGrid_ReportsError(int day, int start, int end)
    {
        IReadOnlyList<string> errors = Errors(Grid(new UiTimeGridEvent("e1", day, start, end, "X")));

        Assert.Single(errors);
    }

    [Fact]
    public void Validate_ZeroLengthAtMidnightBoundary_IsAllowedOnlyForPositiveSpans() =>
        Assert.Empty(Errors(Grid(new UiTimeGridEvent("e1", 0, 1439, 1440, "Late"))));

    [Fact]
    public void Validate_EightDays_ReportsError() =>
        Assert.Equal(["UiTimeGrid.Days must contain 1-7 columns."], Errors(Ui.TimeGrid("g", [.. Week, new UiTimeGridDay("d7", "X")], [])));

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(45)]
    public void Validate_UnsupportedSnap_ReportsError(int snap) =>
        Assert.Equal(["UiTimeGrid.SnapMinutes must be 5, 10, 15, 30 or 60."], Errors(Grid() with { SnapMinutes = snap }));

    [Fact]
    public void Validate_PopoverWithoutDismiss_ReportsError() =>
        Assert.Equal(
            ["UiTimeGrid.Popover requires PopoverAnchorId and DismissId."],
            Errors(Grid(new UiTimeGridEvent("e1", 0, 0, 60, "X")) with { Popover = Ui.Text("p"), PopoverAnchorId = "e1" }));

    [Fact]
    public void Validate_PopoverAnchoredToMissingEvent_ReportsError() =>
        Assert.Equal(
            ["UiTimeGrid.PopoverAnchorId must name one of the events, or the grid itself together with a pending block."],
            Errors(Grid() with { Popover = Ui.Text("p"), PopoverAnchorId = "nope", DismissId = "dismiss" }));

    [Fact]
    public void Validate_NestedTimeGridInPopover_ReportsError() =>
        Assert.Contains(
            "UiTimeGrid.Popover cannot contain another UiTimeGrid.",
            Errors(Grid(new UiTimeGridEvent("e1", 0, 0, 60, "X")) with
            {
                Popover = Ui.Card(Ui.TimeGrid("inner", Week, [])),
                PopoverAnchorId = "e1",
                DismissId = "dismiss",
            }));

    [Fact]
    public void Validate_PartialPendingBlock_ReportsError() =>
        Assert.Equal(
            ["UiTimeGrid.PendingDay, PendingStartMinute and PendingEndMinute must be set together and describe a valid block."],
            Errors(Grid() with { PendingDay = 1 }));

    [Fact]
    public void Validate_DuplicateIdsAcrossEventsAndDays_ReportsError() =>
        Assert.NotEmpty(Errors(Grid(new UiTimeGridEvent("day0", 0, 0, 60, "X"))));

    [Fact]
    public void Validate_MonthCellWithTooManyChips_ReportsError() =>
        Assert.Contains(
            "UiMonthCell.Chips must contain at most 4 labels.",
            Errors(Ui.MonthGrid(["M", "T", "W", "T", "F", "S", "S"], [.. Enumerable.Range(0, 7).Select(i => new UiMonthCell("c" + i, i + 1, Chips: i == 0 ? ["a", "b", "c", "d", "e"] : null))])));
}
