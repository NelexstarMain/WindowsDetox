using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

public sealed class NtPathConverterTests
{
    private static readonly Dictionary<string, string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C:"] = @"\Device\HarddiskVolume3",
        ["D:"] = @"\Device\HarddiskVolume12",
        ["E:"] = @"\??\C:\Subst", // SUBST drive: must be ignored
        ["F:"] = @"\Device\HarddiskVolume3", // duplicate device: first letter wins
        ["Z:"] = @"\Device\LanmanRedirector\;Z:0000000000012345\srv\share",
    };

    private static string? Query(string drive) => Devices.GetValueOrDefault(drive);

    private static IReadOnlyDictionary<string, string> Table() => NtPathConverter.BuildTable(Query);

    [Fact]
    public void BuildTable_IgnoresSubstAndDuplicateDevices()
    {
        var table = Table();

        Assert.Equal(3, table.Count);
        Assert.Equal("C:", table[@"\Device\HarddiskVolume3"]);
        Assert.Equal("D:", table[@"\Device\HarddiskVolume12"]);
        Assert.DoesNotContain(@"\??\C:\Subst", table.Keys);
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe", @"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\device\harddiskvolume3\Windows\x.exe", @"C:\Windows\x.exe")]
    [InlineData(@"\Device\HarddiskVolume12\app.exe", @"D:\app.exe")]
    [InlineData(@"\Device\HarddiskVolume3", "C:")]
    [InlineData(@"\??\C:\Windows\x.exe", @"C:\Windows\x.exe")]
    [InlineData(@"\??\UNC\srv\share\x.exe", @"\\srv\share\x.exe")]
    [InlineData(@"\Device\Mup\srv\share\x.exe", @"\\srv\share\x.exe")]
    [InlineData(@"C:\already\dos.exe", @"C:\already\dos.exe")]
    [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\srv\share\tool.exe", @"Z:\tool.exe")]
    public void Map_ConvertsKnownPaths(string nt, string expected) => Assert.Equal(expected, NtPathConverter.Map(nt, Table()));

    [Theory]
    [InlineData(@"\Device\HarddiskVolume9\x.exe")]
    [InlineData(@"\Device\HarddiskVolume30\x.exe")] // "HarddiskVolume3" is only a string prefix, not a path segment
    [InlineData(@"\SystemRoot\x.exe")]
    [InlineData("")]
    public void Map_ReturnsNullForUnknownDevices(string nt) => Assert.Null(NtPathConverter.Map(nt, Table()));

    [Fact]
    public void Map_PrefersLongestDevicePrefix()
    {
        var table = new Dictionary<string, string>
        {
            [@"\Device\HarddiskVolume3"] = "C:",
            [@"\Device\HarddiskVolume3\Mounted"] = "M:",
        };

        Assert.Equal(@"M:\a.exe", NtPathConverter.Map(@"\Device\HarddiskVolume3\Mounted\a.exe", table));
        Assert.Equal(@"C:\Other\a.exe", NtPathConverter.Map(@"\Device\HarddiskVolume3\Other\a.exe", table));
    }

    [Fact]
    public void Convert_RefreshesTableOnUnknownDeviceButNotMoreThanEveryFiveSeconds()
    {
        var clock = 0L;
        var queries = 0;
        var mounted = false;
        var converter = new NtPathConverter(
            drive =>
            {
                queries++;
                return drive == "C:" ? @"\Device\HarddiskVolume3" : drive == "G:" && mounted ? @"\Device\HarddiskVolume20" : null;
            },
            () => clock);
        var afterConstruction = queries;

        mounted = true;
        clock = 1_000;
        Assert.Null(converter.Convert(@"\Device\HarddiskVolume20\x.exe")); // too soon to refresh
        Assert.Equal(afterConstruction, queries);

        clock = 6_000;
        Assert.Equal(@"G:\x.exe", converter.Convert(@"\Device\HarddiskVolume20\x.exe"));
        Assert.True(queries > afterConstruction);
        Assert.Equal(@"C:\y.exe", converter.Convert(@"\Device\HarddiskVolume3\y.exe"));
    }
}
