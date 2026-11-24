using Blocker.Core.PluginRuntime;
using Blocker.UI.Sdk;

namespace Blocker.Core.Tests.PluginRuntime;

public class UiActionValidatorTests
{
    private static UiAction Schedule(string id, params (string Key, string Value)[] values) =>
        new(id, UiActionKind.Schedule, null, values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void IsValid_ScheduleWithDayStartEnd_Accepted() =>
        Assert.True(UiActionValidator.IsValid(Schedule("grid", ("day", "6"), ("start", "540"), ("end", "1440"))));

    [Theory]
    [InlineData("7", "0", "60")]
    [InlineData("0", "60", "60")]
    [InlineData("0", "0", "1441")]
    [InlineData("0", "-5", "60")]
    [InlineData("0", "+5", "60")]
    [InlineData("0", "5 ", "60")]
    [InlineData("0", "00000", "60")]
    public void IsValid_ScheduleOutOfRangeOrMalformed_Rejected(string day, string start, string end) =>
        Assert.False(UiActionValidator.IsValid(Schedule("grid", ("day", day), ("start", start), ("end", end))));

    [Fact]
    public void IsValid_ScheduleWithExtraValue_Rejected() =>
        Assert.False(UiActionValidator.IsValid(Schedule("grid", ("day", "0"), ("start", "0"), ("end", "60"), ("x", "1"))));

    [Fact]
    public void IsValid_ClickWithExtendFlag_Accepted() =>
        Assert.True(UiActionValidator.IsValid(new UiAction("e1", UiActionKind.Click, null, new Dictionary<string, string> { ["extend"] = "true" })));

    [Fact]
    public void IsValid_ClickWithOtherValues_Rejected() =>
        Assert.False(UiActionValidator.IsValid(new UiAction("e1", UiActionKind.Click, null, new Dictionary<string, string> { ["name"] = "x" })));

    private static UiAction Pick(params (string Key, string Value)[] values) =>
        new("forest", UiActionKind.Pick, null, values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void IsValid_PickWithColumnAndRow_Accepted() =>
        Assert.True(UiActionValidator.IsValid(Pick(("column", "0"), ("row", "23"))));

    [Theory]
    [InlineData("24", "0")]
    [InlineData("-1", "0")]
    [InlineData("1.5", "0")]
    [InlineData("", "0")]
    public void IsValid_PickOutOfRangeOrMalformed_Rejected(string column, string row) =>
        Assert.False(UiActionValidator.IsValid(Pick(("column", column), ("row", row))));

    [Fact]
    public void IsValid_PickWithValueOrExtraField_Rejected()
    {
        Assert.False(UiActionValidator.IsValid(Pick(("column", "1"), ("row", "1"), ("x", "1"))));
        Assert.False(UiActionValidator.IsValid(new UiAction("forest", UiActionKind.Pick, "1", new Dictionary<string, string> { ["column"] = "1", ["row"] = "1" })));
    }
}
