using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Sdk.Extensions;

namespace Blocker.Core.Tests.Policy;

public sealed class EnforcementPolicyTests
{
    private const string Win = @"C:\Windows";
    private const string Install = @"C:\Program Files\Blocker";
    private const string IdeHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CopiedCmdHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string IdePath = @"C:\Program Files\Ide\ide.exe";

    private static EnforcementPolicy Policy(AllowlistEntry[]? entries = null, string[]? bypassHashes = null) =>
        new(
            new CriticalProcesses(Win, Install),
            new BypassTools(bypassHashes ?? [CopiedCmdHash]),
            new Allowlist(entries ?? [new AllowlistEntry("IDE", IdePath, IdeHash), new AllowlistEntry("Notes", @"C:\Tools\notes.exe"), new AllowlistEntry("Store app", PackageFamilyName: "Contoso.App_8wekyb3d8bbwe")]),
            Win);

    private static ProcessFacts Facts(
        string path,
        int session = 1,
        string? sha = null,
        bool signed = false,
        string? pfn = null,
        bool parent = false,
        EnforcementMode mode = EnforcementMode.Active) =>
        new(new ProcessInfo(100, 4, session, path, 10), sha, signed, pfn, parent, mode);

    private static void Expect(ProcessFacts facts, VerdictKind kind, VerdictReason reason)
    {
        ProcessVerdict verdict = Policy().Decide(facts);
        Assert.Equal(new ProcessVerdict(kind, reason), verdict);
    }

    [Fact]
    public void Session0_is_never_touched_even_for_bypass_tools()
    {
        Expect(Facts(@"C:\Windows\System32\cmd.exe", session: 0), VerdictKind.Allow, VerdictReason.Session0);
        Expect(Facts(@"C:\Temp\evil.exe", session: 0), VerdictKind.Allow, VerdictReason.Session0);
    }

    [Fact]
    public void Critical_process_is_allowed_before_everything_else()
    {
        Expect(Facts(@"C:\Windows\explorer.exe"), VerdictKind.Allow, VerdictReason.Critical);
        Expect(Facts(@"C:\Windows\explorer.exe", mode: EnforcementMode.NoSession), VerdictKind.Allow, VerdictReason.Critical);
        Expect(Facts(@"C:\Windows\explorer.exe", mode: EnforcementMode.EmergencyPass), VerdictKind.Allow, VerdictReason.Critical);
    }

    [Fact]
    public void Renamed_explorer_outside_the_windows_directory_is_not_critical()
    {
        Expect(Facts(@"C:\Users\me\Downloads\explorer.exe"), VerdictKind.Block, VerdictReason.NotAllowlisted);
        Expect(Facts(@"C:\Windows\..\Users\me\explorer.exe", signed: true), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Fact]
    public void Blocker_own_executables_are_allowed_only_from_the_install_directory()
    {
        Expect(Facts(Install + @"\Blocker.Stub.exe"), VerdictKind.Allow, VerdictReason.BlockerOwn);
        Expect(Facts(@"C:\Users\me\Blocker.Stub.exe"), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Fact]
    public void Emergency_pass_and_missing_session_allow_unknown_processes()
    {
        Expect(Facts(@"C:\Temp\game.exe", mode: EnforcementMode.EmergencyPass), VerdictKind.Allow, VerdictReason.EmergencyPass);
        Expect(Facts(@"C:\Temp\game.exe", mode: EnforcementMode.NoSession), VerdictKind.Allow, VerdictReason.NoSession);
    }

    [Fact]
    public void No_session_and_emergency_pass_come_after_critical_and_before_bypass()
    {
        Expect(Facts(@"C:\Windows\System32\cmd.exe", mode: EnforcementMode.NoSession), VerdictKind.Allow, VerdictReason.NoSession);
        Expect(Facts(@"C:\Windows\System32\cmd.exe", mode: EnforcementMode.EmergencyPass), VerdictKind.Allow, VerdictReason.EmergencyPass);
    }

    [Fact]
    public void Bypass_tool_is_blocked_even_when_signed_baseline_allowlisted_or_inherited()
    {
        Expect(Facts(@"C:\Windows\System32\taskmgr.exe", signed: true), VerdictKind.Block, VerdictReason.BypassTool);
        Expect(Facts(@"C:\Windows\System32\cmd.exe", signed: true, parent: true), VerdictKind.Block, VerdictReason.BypassTool);
        ProcessVerdict allowlisted = Policy([new AllowlistEntry("cmd", @"C:\Windows\System32\cmd.exe")]).Decide(Facts(@"C:\Windows\System32\cmd.exe"));
        Assert.Equal(new ProcessVerdict(VerdictKind.Block, VerdictReason.BypassTool), allowlisted);
    }

    [Fact]
    public void Renamed_copy_of_a_bypass_tool_is_caught_by_hash()
    {
        Expect(Facts(@"C:\Users\me\calc2.exe", sha: CopiedCmdHash.ToUpperInvariant()), VerdictKind.Block, VerdictReason.BypassTool);
    }

    [Fact]
    public void Bypass_tool_started_by_an_allowed_ide_is_blocked()
    {
        var tree = new ProcessTree();
        var ide = new ProcessInfo(200, 4, 1, IdePath, 100);
        ProcessVerdict ideVerdict = Policy().Decide(new ProcessFacts(ide, IdeHash));
        tree.Add(ide, ideVerdict.AllowsInheritance);
        var shell = new ProcessInfo(201, 200, 1, @"C:\Windows\System32\powershell.exe", 200);

        ProcessVerdict verdict = Policy().Decide(new ProcessFacts(shell, null, true, null, tree.ParentAllowsInheritance(shell)));

        Assert.Equal(VerdictReason.AllowlistHash, ideVerdict.Reason);
        Assert.Equal(new ProcessVerdict(VerdictKind.Block, VerdictReason.BypassTool), verdict);
    }

    [Fact]
    public void Allowlist_matches_by_hash_regardless_of_case_and_location()
    {
        Expect(Facts(@"D:\portable\ide.exe", sha: IdeHash.ToUpperInvariant()), VerdictKind.Allow, VerdictReason.AllowlistHash);
    }

    [Fact]
    public void Path_with_a_hash_entry_requires_the_hash()
    {
        Expect(Facts(IdePath, sha: new string('c', 64)), VerdictKind.Block, VerdictReason.NotAllowlisted);
        Expect(Facts(IdePath, sha: null), VerdictKind.Block, VerdictReason.NotAllowlisted);
        Expect(Facts(IdePath, sha: IdeHash), VerdictKind.Allow, VerdictReason.AllowlistHash);
    }

    [Theory]
    [InlineData(@"C:\Tools\notes.exe")]
    [InlineData(@"c:/tools/NOTES.EXE")]
    [InlineData(@"\\?\C:\Tools\.\notes.exe")]
    public void Path_only_entry_matches_normalized_paths(string path)
    {
        Expect(Facts(path), VerdictKind.Allow, VerdictReason.AllowlistPath);
    }

    [Fact]
    public void Path_only_entry_does_not_match_other_directories_or_traversal()
    {
        Expect(Facts(@"C:\Tools\sub\notes.exe"), VerdictKind.Block, VerdictReason.NotAllowlisted);
        Expect(Facts(@"C:\Tools\..\Temp\notes.exe"), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Fact]
    public void Allowlist_matches_by_package_family_name()
    {
        Expect(Facts(@"C:\Program Files\WindowsApps\Contoso\app.exe", pfn: "contoso.app_8wekyb3d8bbwe"), VerdictKind.Allow, VerdictReason.AllowlistPackage);
        Expect(Facts(@"C:\Program Files\WindowsApps\Other\app.exe", pfn: "Other.App_1"), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Fact]
    public void Inheritance_allows_children_of_allowlisted_parents_but_not_bypass_tools()
    {
        Expect(Facts(@"C:\Temp\helper.exe", parent: true), VerdictKind.Allow, VerdictReason.InheritedFromAllowed);
    }

    [Fact]
    public void Inheritance_chain_follows_allowlist_and_inherited_verdicts_only()
    {
        EnforcementPolicy policy = Policy();
        var tree = new ProcessTree();
        var ide = new ProcessInfo(200, 4, 1, IdePath, 100);
        tree.Add(ide, policy.Decide(new ProcessFacts(ide, IdeHash)).AllowsInheritance);
        var child = new ProcessInfo(201, 200, 1, @"C:\Temp\child.exe", 200);
        ProcessVerdict childVerdict = policy.Decide(new ProcessFacts(child, ParentAllowsInheritance: tree.ParentAllowsInheritance(child)));
        tree.Add(child, childVerdict.AllowsInheritance);
        var grandChild = new ProcessInfo(202, 201, 1, @"C:\Temp\grand.exe", 300);

        ProcessVerdict grandVerdict = policy.Decide(new ProcessFacts(grandChild, ParentAllowsInheritance: tree.ParentAllowsInheritance(grandChild)));

        Assert.Equal(VerdictReason.InheritedFromAllowed, childVerdict.Reason);
        Assert.Equal(VerdictReason.InheritedFromAllowed, grandVerdict.Reason);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe", true)]
    [InlineData(@"C:\Windows\System32\notepad.exe", true)]
    [InlineData(@"C:\Windows\System32\notepad.exe", false)]
    public void Inheritance_is_denied_below_critical_and_baseline_parents(string parentPath, bool signed)
    {
        EnforcementPolicy policy = Policy();
        var tree = new ProcessTree();
        var parent = new ProcessInfo(300, 4, 1, parentPath, 100);
        ProcessVerdict parentVerdict = policy.Decide(new ProcessFacts(parent, null, signed));
        tree.Add(parent, parentVerdict.AllowsInheritance);
        var child = new ProcessInfo(301, 300, 1, @"C:\Temp\payload.exe", 200);

        ProcessVerdict childVerdict = policy.Decide(new ProcessFacts(child, ParentAllowsInheritance: tree.ParentAllowsInheritance(child)));

        Assert.False(parentVerdict.AllowsInheritance);
        Assert.Equal(new ProcessVerdict(VerdictKind.Block, VerdictReason.NotAllowlisted), childVerdict);
    }

    [Fact]
    public void Inheritance_from_a_system_baseline_parent_is_denied()
    {
        EnforcementPolicy policy = Policy();
        var tree = new ProcessTree();
        var parent = new ProcessInfo(300, 4, 1, @"C:\Windows\System32\notepad.exe", 100);
        ProcessVerdict parentVerdict = policy.Decide(new ProcessFacts(parent, null, true));
        tree.Add(parent, parentVerdict.AllowsInheritance);
        var child = new ProcessInfo(301, 300, 1, @"C:\Temp\payload.exe", 200);

        Assert.Equal(VerdictReason.SystemBaseline, parentVerdict.Reason);
        Assert.False(tree.ParentAllowsInheritance(child));
    }

    [Fact]
    public void Reused_parent_pid_does_not_pass_inheritance_on()
    {
        EnforcementPolicy policy = Policy();
        var tree = new ProcessTree();
        var ide = new ProcessInfo(200, 4, 1, IdePath, 100);
        tree.Add(ide, policy.Decide(new ProcessFacts(ide, IdeHash)).AllowsInheritance);
        tree.MarkExited(200, 100);
        var reused = new ProcessInfo(200, 4, 1, @"C:\Temp\unrelated.exe", 500);
        tree.Add(reused, policy.Decide(new ProcessFacts(reused)).AllowsInheritance);
        var oldChild = new ProcessInfo(201, 200, 1, @"C:\Temp\child.exe", 300);

        ProcessVerdict verdict = policy.Decide(new ProcessFacts(oldChild, ParentAllowsInheritance: tree.ParentAllowsInheritance(oldChild)));

        Assert.Equal(new ProcessVerdict(VerdictKind.Block, VerdictReason.NotAllowlisted), verdict);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Windows\SysWOW64\notepad.exe")]
    [InlineData(@"C:\Windows\SystemApps\Microsoft.Windows.Cortana_x\app.exe")]
    public void System_baseline_requires_a_microsoft_signature(string path)
    {
        Expect(Facts(path, signed: true), VerdictKind.Allow, VerdictReason.SystemBaseline);
        Expect(Facts(path, signed: false), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"C:\Windows\System32evil\a.exe")]
    [InlineData(@"C:\Windows\System32\..\..\Users\me\a.exe")]
    [InlineData(@"C:\Program Files\Microsoft\a.exe")]
    [InlineData(@"")]
    public void System_baseline_is_limited_to_the_system_directories(string path)
    {
        Expect(Facts(path, signed: true), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Fact]
    public void Unknown_process_is_blocked()
    {
        Expect(Facts(@"C:\Games\game.exe"), VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    [Fact]
    public void Allowlist_rejects_unusable_entries()
    {
        Assert.Throws<ArgumentException>(() => new Allowlist([new AllowlistEntry("empty")]));
        Assert.Throws<ArgumentException>(() => new Allowlist([new AllowlistEntry("bad", Sha256: "xyz")]));
        Assert.Throws<ArgumentException>(() => new Allowlist([new AllowlistEntry("bad", Sha256: new string('g', 64))]));
    }

    [Fact]
    public void Plugins_can_turn_allow_into_block()
    {
        ProcessVerdict combined = EnforcementPolicy.CombineWithPlugins(
            new ProcessVerdict(VerdictKind.Allow, VerdictReason.AllowlistHash),
            [InterceptDecision.Abstain, InterceptDecision.Block("social media")]);

        Assert.Equal(new ProcessVerdict(VerdictKind.Block, VerdictReason.PluginInterceptor, "social media"), combined);
    }

    [Theory]
    [InlineData(VerdictReason.NoSession)]
    [InlineData(VerdictReason.EmergencyPass)]
    [InlineData(VerdictReason.SystemBaseline)]
    [InlineData(VerdictReason.InheritedFromAllowed)]
    [InlineData(VerdictReason.AllowlistPackage)]
    public void Plugins_can_tighten_every_other_allow(VerdictReason reason)
    {
        ProcessVerdict combined = EnforcementPolicy.CombineWithPlugins(new ProcessVerdict(VerdictKind.Allow, reason), [InterceptDecision.Block("x")]);

        Assert.Equal(VerdictKind.Block, combined.Kind);
    }

    [Theory]
    [InlineData(VerdictReason.Session0)]
    [InlineData(VerdictReason.Critical)]
    [InlineData(VerdictReason.BlockerOwn)]
    public void Plugins_can_never_block_session0_critical_or_own_processes(VerdictReason reason)
    {
        var core = new ProcessVerdict(VerdictKind.Allow, reason);

        Assert.Equal(core, EnforcementPolicy.CombineWithPlugins(core, [InterceptDecision.Block("x")]));
    }

    [Fact]
    public void Plugins_cannot_turn_block_into_allow_and_abstaining_keeps_the_verdict()
    {
        var block = new ProcessVerdict(VerdictKind.Block, VerdictReason.NotAllowlisted);
        var allow = new ProcessVerdict(VerdictKind.Allow, VerdictReason.AllowlistPath);

        Assert.Equal(block, EnforcementPolicy.CombineWithPlugins(block, [InterceptDecision.Abstain]));
        Assert.Equal(allow, EnforcementPolicy.CombineWithPlugins(allow, [InterceptDecision.Abstain, InterceptDecision.Abstain]));
        Assert.Equal(allow, EnforcementPolicy.CombineWithPlugins(allow, []));
    }

    [Fact]
    public void Only_allowlist_and_inheritance_verdicts_allow_inheritance()
    {
        foreach (VerdictReason reason in Enum.GetValues<VerdictReason>())
        {
            bool expected = reason is VerdictReason.AllowlistHash or VerdictReason.AllowlistPath or VerdictReason.AllowlistPackage or VerdictReason.InheritedFromAllowed;

            Assert.Equal(expected, new ProcessVerdict(VerdictKind.Allow, reason).AllowsInheritance);
            Assert.False(new ProcessVerdict(VerdictKind.Block, reason).AllowsInheritance);
        }
    }
}
