using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class PluginDataSecurityTests
{
    [Fact]
    public void Sddl_labels_the_directory_low_and_protects_the_dacl()
    {
        Assert.Contains("S:(ML;OICI;NW;;;LW)", PluginDataSecurity.Sddl, StringComparison.Ordinal);
        Assert.StartsWith("D:P", PluginDataSecurity.Sddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(A;OICI;FA;;;SY)")]
    [InlineData("(A;OICI;FA;;;BA)")]
    [InlineData("(A;OICI;0x1301BF;;;BU)")]
    public void Sddl_grants_system_administrators_and_the_plugin_host(string ace) =>
        Assert.Contains(ace, PluginDataSecurity.Sddl, StringComparison.Ordinal);
}
