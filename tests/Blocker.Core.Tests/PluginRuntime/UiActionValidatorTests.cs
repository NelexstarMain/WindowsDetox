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
}
