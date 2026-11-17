namespace Blocker.Platform.Windows.Tests;

public sealed class MarkerTests
{
    [Fact]
    public void MarkerValueName_IsStable() => Assert.Equal("BlockerOwned", PlatformMarker.MarkerValueName);
}
