using Blocker.Core.Orchestration;
using Blocker.Platform.Windows.Changes;
using Blocker.Platform.Windows.Planning;
using Blocker.Platform.Windows.Tests.Changes;

namespace Blocker.Platform.Windows.Tests.Planning;

public sealed class WindowsChangePlannerTests
{
    private const string Stub = @"C:\Program Files\WindowsDetox\Blocker.Stub.exe";
    private const string Install = @"C:\Program Files\WindowsDetox";
    private const string Data = @"C:\ProgramData\WindowsDetox";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static SessionPlanInput Input(
        int port = 18080,
        string[]? names = null,
        string[]? directories = null,
        string[]? services = null) =>
        new(
            port,
            names ?? ["game.exe", "chat.exe"],
            Stub,
            directories ?? [Install, Data],
            services ?? ["WindowsDetoxService", "WindowsDetoxWatchdog"]);

    [Fact]
    public void The_plan_is_ifeo_then_browser_policies_then_directory_denies_then_service_dacls()
    {
        IReadOnlyList<PlannedChange> plan = new WindowsChangePlanner().PlanSessionChanges(Input());

        string[] kinds = [.. plan.Select(static c => c.Kind)];
        int policyCount = BrowserPolicyPlan.Create(18080).Count;
        Assert.Equal([.. Enumerable.Repeat("ifeo", 2), .. Enumerable.Repeat("regpolicy", policyCount), "ntfsdeny", "ntfsdeny", "servicedacl", "servicedacl"], kinds);
    }

    [Fact]
    public void Every_image_name_redirects_to_the_stub_and_names_are_sorted_and_distinct()
    {
        IReadOnlyList<PlannedChange> plan = new WindowsChangePlanner().PlanSessionChanges(Input(names: ["Zoom.exe", "game.exe", "GAME.EXE", "chat.exe"]));

        PlannedChange[] ifeo = [.. plan.Where(static c => c.Kind == "ifeo")];
        Assert.Equal(["chat.exe", "game.exe", "Zoom.exe"], ifeo.Select(static c => c.Target));
        Assert.All(ifeo, c => Assert.Equal(Stub, ChangeGuard.FromJson(c.DesiredStateJson, ChangesJsonContext.Default.IfeoDesiredState, "json").StubPath));
    }

    [Fact]
    public void Names_the_ifeo_handler_would_refuse_are_left_out_instead_of_failing_the_session()
    {
        var planner = new WindowsChangePlanner(["explorer.exe"]);

        IReadOnlyList<PlannedChange> plan = planner.PlanSessionChanges(Input(names: ["ok.exe", "explorer.exe", "EXPLORER.EXE", @"dir\evil.exe", "noextension", "bad:name.exe", "star*.exe", ".exe", " lead.exe", "dots..exe"]));

        Assert.Equal(["ok.exe"], plan.Where(static c => c.Kind == "ifeo").Select(static c => c.Target));
    }

    [Fact]
    public void Browser_policies_point_at_the_proxy_port_of_the_session()
    {
        IReadOnlyList<PlannedChange> plan = new WindowsChangePlanner().PlanSessionChanges(Input(port: 23456));

        PlannedChange[] policies = [.. plan.Where(static c => c.Kind == "regpolicy")];
        Assert.Equal(BrowserPolicyPlan.Create(23456).Select(static e => e.Target), policies.Select(static c => c.Target));
        Assert.Equal(BrowserPolicyPlan.Create(23456).Select(static e => e.DesiredStateJson), policies.Select(static c => c.DesiredStateJson));
        Assert.Contains(policies, c => c.DesiredStateJson.Contains("127.0.0.1:23456", StringComparison.Ordinal));
    }

    [Fact]
    public void Directories_are_normalized_and_listed_once()
    {
        IReadOnlyList<PlannedChange> plan = new WindowsChangePlanner().PlanSessionChanges(Input(directories: [Install + "\\", @"c:\program files\windowsdetox", Data]));

        Assert.Equal([Install, Data], plan.Where(static c => c.Kind == "ntfsdeny").Select(static c => c.Target));
    }

    [Fact]
    public void Both_services_get_the_restricted_dacl()
    {
        IReadOnlyList<PlannedChange> plan = new WindowsChangePlanner().PlanSessionChanges(Input());

        PlannedChange[] services = [.. plan.Where(static c => c.Kind == "servicedacl")];
        Assert.Equal(["WindowsDetoxService", "WindowsDetoxWatchdog"], services.Select(static c => c.Target));
        Assert.All(services, c => Assert.Null(ChangeGuard.FromJson(c.DesiredStateJson, ChangesJsonContext.Default.ServiceDaclDesiredState, "json").Sddl));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void An_invalid_proxy_port_is_refused(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsChangePlanner().PlanSessionChanges(Input(port: port)));

    [Fact]
    public void Misconfigured_paths_and_service_names_fail_loudly()
    {
        var planner = new WindowsChangePlanner();

        Assert.Throws<ArgumentException>(() => planner.PlanSessionChanges(Input(directories: [@"C:\a\..\b"])));
        Assert.Throws<ArgumentException>(() => planner.PlanSessionChanges(Input(directories: [@"\\server\share"])));
        Assert.Throws<ArgumentException>(() => planner.PlanSessionChanges(Input(services: [@"bad\name"])));
        Assert.Throws<ArgumentException>(() => planner.PlanSessionChanges(Input() with { StubExecutablePath = "Blocker.Stub.exe" }));
    }

    [Fact]
    public async Task Every_planned_change_is_accepted_by_the_real_handlers()
    {
        IReadOnlyList<PlannedChange> plan = new WindowsChangePlanner(["explorer.exe"]).PlanSessionChanges(Input());
        var ifeo = new FakeIfeoStore();
        var policies = new FakePolicyStore();
        var acl = new FakeDirectoryAcl();
        acl.Add(Install, "D:PAI(A;OICI;FA;;;SY)");
        acl.Add(Data, "D:PAI(A;OICI;FA;;;SY)");
        var security = new FakeServiceSecurity();
        security.Sddl["WindowsDetoxService"] = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)";
        security.Sddl["WindowsDetoxWatchdog"] = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)";
        Dictionary<string, Blocker.Core.Journal.IReversibleChangeHandler> handlers = new[]
        {
            (Blocker.Core.Journal.IReversibleChangeHandler)new IfeoChangeHandler("install-1", ["explorer.exe"], ifeo),
            new RegistryPolicyChangeHandler("install-1", policies, RegistryPolicyChangeHandler.DefaultRoots),
            new NtfsDenyWriteChangeHandler([Install, Data], acl, new FakeDenyTransform()),
            new ServiceDaclChangeHandler(security, null),
        }.ToDictionary(static h => h.Kind);

        foreach (PlannedChange change in plan)
        {
            await handlers[change.Kind].ApplyAsync(change.Target, change.DesiredStateJson, Ct);
        }

        Assert.Equal(2, plan.Count(static c => c.Kind == "ifeo"));
        Assert.Contains("\"" + Stub + "\" --ifeo", ifeo.Values(IfeoView.Native, "game.exe")!["Debugger"], StringComparison.Ordinal);
        Assert.All(acl.Directories.Values, d => Assert.Contains(FakeDenyTransform.Tag, d.Sddl, StringComparison.Ordinal));
    }
}
