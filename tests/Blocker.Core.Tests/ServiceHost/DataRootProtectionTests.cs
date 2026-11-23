using System.ComponentModel;
using Blocker.Core.ServiceHost;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class DataRootProtectionTests
{
    [Fact]
    public async Task The_data_root_is_protected_at_every_start_after_it_exists_and_before_state_is_read()
    {
        var calls = new List<(bool RootExisted, bool InstallIdExisted)>();
        await using ServiceHostHarness harness = await ServiceHostHarness.StartAsync(arrange: h => h.ProtectDataRoot = paths =>
            calls.Add((h.Fs.Directory.Exists(paths.DataRoot), h.Fs.File.Exists(paths.InstallIdPath))));

        await harness.RestartAsync();

        Assert.Equal([(true, false), (true, true)], calls); // the first start protected the root before anything was written into it
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public async Task A_failing_protection_is_logged_and_does_not_stop_the_service(Type failure)
    {
        await using ServiceHostHarness harness = await ServiceHostHarness.StartAsync(
            arrange: h => h.ProtectDataRoot = _ => throw (Exception)Activator.CreateInstance(failure)!);

        Assert.NotNull(harness.App);
    }
}
