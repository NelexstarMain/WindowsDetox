using Blocker.Sdk.Events;

namespace Blocker.SDK.Tests;

public class PluginCapabilitiesTests
{
    public static TheoryData<PluginCapability> AllCapabilities => [.. Enum.GetValues<PluginCapability>()];

    [Theory]
    [MemberData(nameof(AllCapabilities))]
    public void TryParse_ManifestNameOfEveryCapability_RoundTrips(PluginCapability capability)
    {
        string name = PluginCapabilities.ToManifestName(capability);

        bool ok = PluginCapabilities.TryParse(name, out PluginCapability parsed);

        Assert.True(ok);
        Assert.Equal(capability, parsed);
    }

    [Theory]
    [InlineData(PluginCapability.SessionEvents, "events.session")]
    [InlineData(PluginCapability.WindowTitles, "events.window.title")]
    [InlineData(PluginCapability.Tick, "tick")]
    [InlineData(PluginCapability.UiBlockScreen, "ui.blockscreen")]
    [InlineData(PluginCapability.Network, "network")]
    public void ToManifestName_KnownCapability_ReturnsExactName(PluginCapability capability, string expected) =>
        Assert.Equal(expected, PluginCapabilities.ToManifestName(capability));

    [Fact]
    public void ToManifestName_UndefinedValue_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginCapabilities.ToManifestName((PluginCapability)999));

    [Fact]
    public void AllNames_Always_UniqueAndCountEqualsEnumCount()
    {
        Assert.Equal(Enum.GetValues<PluginCapability>().Length, PluginCapabilities.AllNames.Count);
        Assert.Equal(PluginCapabilities.AllNames.Count, PluginCapabilities.AllNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Tick")]
    [InlineData("EVENTS.SESSION")]
    [InlineData("events.Process")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_UnknownOrWrongCase_ReturnsFalse(string? name)
    {
        bool ok = PluginCapabilities.TryParse(name, out PluginCapability capability);

        Assert.False(ok);
        Assert.Equal(default, capability);
    }

    [Theory]
    [InlineData(typeof(SessionStarted), PluginCapability.SessionEvents)]
    [InlineData(typeof(SessionEnded), PluginCapability.SessionEvents)]
    [InlineData(typeof(EmergencyPassStarted), PluginCapability.SessionEvents)]
    [InlineData(typeof(EmergencyPassEnded), PluginCapability.SessionEvents)]
    [InlineData(typeof(ProcessBlocked), PluginCapability.ProcessEvents)]
    [InlineData(typeof(ActiveWindowChanged), PluginCapability.WindowEvents)]
    [InlineData(typeof(UserIdleChanged), PluginCapability.IdleEvents)]
    [InlineData(typeof(CustomPluginEvent), PluginCapability.CustomEvents)]
    public void RequiredForEvent_CoreEventType_ReturnsCapability(Type eventType, PluginCapability expected) =>
        Assert.Equal(expected, PluginCapabilities.RequiredForEvent(eventType));

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(IBlockerEvent))]
    public void RequiredForEvent_NonEventType_ThrowsArgumentException(Type eventType)
    {
        var ex = Assert.Throws<ArgumentException>(() => PluginCapabilities.RequiredForEvent(eventType));

        Assert.Equal("eventType", ex.ParamName);
    }

    [Fact]
    public void RequiredForEvent_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => PluginCapabilities.RequiredForEvent(null!));
}
