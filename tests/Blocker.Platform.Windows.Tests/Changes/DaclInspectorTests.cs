using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class DaclInspectorTests
{
    // What Windows reports for C:\Program Files\<dir>: only SYSTEM, Administrators, TrustedInstaller and Creator Owner (inherit-only) may modify.
    private const string ProgramFilesDacl =
        "D:PAI(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;CIIO;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)"
        + "(A;;FA;;;SY)(A;OICIIO;GA;;;SY)(A;;FA;;;BA)(A;OICIIO;GA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;GXGR;;;BU)(A;;0x1200a9;;;AC)(A;OICIIO;GXGR;;;AC)(A;OICIIO;GA;;;CO)";

    [Theory]
    [InlineData(ProgramFilesDacl, true)]
    [InlineData("D:", true)]
    [InlineData("D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", true)]
    [InlineData("D:(A;;GA;;;WD)", false)]
    [InlineData("D:(A;OICI;FA;;;SY)(A;OICI;GA;;;BU)", false)]
    [InlineData("D:(A;OICI;0x1301bf;;;BU)", false)] // modify
    [InlineData("D:(A;;DC;;;AU)", false)] // write data
    [InlineData("D:(A;;WD;;;IU)", false)] // write DAC
    [InlineData("D:(A;;SD;;;S-1-5-21-1-2-3-1001)", false)] // delete, specific user
    [InlineData("D:(A;;0x02000000;;;BU)", false)] // maximum allowed
    [InlineData("D:(A;;GA;;;CO)", false)] // Creator Owner applies to the object itself unless the ACE is inherit-only
    [InlineData("D:(A;CIIO;GA;;;CO)", true)]
    [InlineData("D:(D;;GA;;;BU)", true)] // a deny ACE grants nothing
    [InlineData("D:(OA;;GA;;;;BU)", false)] // object ACEs are not understood
    [InlineData("D:(A;;ZZ;;;BU)", false)] // unknown right token
    [InlineData("D:(A;;GA;;;)", false)]
    [InlineData("O:SYG:SYD:(A;;GA;;;SY)", false)]
    [InlineData("D:(A;;GA;;;SY)S:(AU;;GA;;;WD)", false)]
    [InlineData("D:(A;;GA;;;SY", false)]
    [InlineData("", false)]
    public void Grants_nothing_dangerous_is_decided_per_ace(string sddl, bool expected) =>
        Assert.Equal(expected, DaclInspector.GrantsNothingDangerousToUnprivileged(sddl, DaclInspector.FileModify));

    [Fact]
    public void Inherit_only_is_read_from_two_letter_flag_codes_not_substrings()
    {
        // "CIOI" contains the characters "IO" across two codes (CI, OI); it is not inherit-only.
        Assert.False(DaclInspector.GrantsNothingDangerousToUnprivileged("D:(A;CIOI;GA;;;CO)", DaclInspector.FileModify));
        Assert.True(DaclInspector.GrantsNothingDangerousToUnprivileged("D:(A;OICIIO;GA;;;CO)", DaclInspector.FileModify));
    }

    [Fact]
    public void An_ace_already_present_in_the_baseline_is_not_a_new_grant()
    {
        const string current = "D:PAI(A;OICIID;FA;;;SY)(A;OICIID;FA;;;S-1-5-21-1-2-3-1001)";

        Assert.False(DaclInspector.GrantsNothingDangerousToUnprivileged(current, DaclInspector.FileModify));
        Assert.True(DaclInspector.GrantsNothingDangerousToUnprivileged(current, DaclInspector.FileModify, current));
        Assert.False(DaclInspector.GrantsNothingDangerousToUnprivileged(current + "(A;;GA;;;WD)", DaclInspector.FileModify, current));
        Assert.False(DaclInspector.GrantsNothingDangerousToUnprivileged(current, DaclInspector.FileModify, "not sddl"));
    }

    [Theory]
    [InlineData("D:(A;;CCLCSWLOCRRC;;;IU)", true)] // query only
    [InlineData("D:(A;;CCLCSWRPLORC;;;AU)", true)] // plus start
    [InlineData("D:(A;;CCDC;;;IU)", false)] // change config
    [InlineData("D:(A;;WP;;;IU)", false)] // stop
    [InlineData("D:(A;;SD;;;BU)", false)]
    [InlineData("D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)", true)]
    public void Service_rights_are_judged_with_the_service_mask(string sddl, bool expected) =>
        Assert.Equal(expected, DaclInspector.GrantsNothingDangerousToUnprivileged(sddl, DaclInspector.ServiceControl));
}
