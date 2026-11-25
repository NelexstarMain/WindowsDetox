using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Ports;
using Blocker.Core.ServiceHost;
using Blocker.Core.Tests.AppRules;
using Blocker.Core.Tests.Orchestration;
using Blocker.Sdk.Events;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class AppRuleHandlerTests
{
    private const string UnrelatedPath = @"C:\Apps\unrelated.exe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<OperationResponse> SetSetsAsync(IpcClient client, params AppSetDto[] sets) =>
        client.InvokeAsync<AppSetsDto, OperationResponse>(IpcMethods.AppSetsSet, new AppSetsDto(sets), Ct);

    private static ValueTask<OperationResponse> SetRulesAsync(IpcClient client, params AppRuleDto[] rules) =>
        client.InvokeAsync<AppRulesDto, OperationResponse>(IpcMethods.AppRulesSet, new AppRulesDto(rules), Ct);

    private static ValueTask<AppRulesStatusResponse> StatusAsync(IpcClient client) =>
        client.InvokeAsync<AppRulesStatusResponse>(IpcMethods.AppRulesStatus, Ct);

    private static async Task<IpcErrorCode> CodeOfAsync(Func<ValueTask> call)
    {
        IpcException error = await Assert.ThrowsAsync<IpcException>(async () => await call());
        return error.Code;
    }

    [Fact]
    public async Task App_sets_are_saved_returned_in_order_and_kept_across_a_restart()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.Empty((await client.InvokeAsync<AppSetsDto>(IpcMethods.AppSetsGet, Ct)).Sets);

        OperationResponse saved = await SetSetsAsync(client, new AppSetDto(" Deep work ", [Game], ["example.com"]), new AppSetDto("Chat", [], []));
        await h.RestartAsync();
        IpcClient again = await h.ConnectAsync(IpcCallerRole.User);
        AppSetsDto read = await again.InvokeAsync<AppSetsDto>(IpcMethods.AppSetsGet, Ct);

        Assert.True(saved.Success);
        Assert.Equal(["Deep work", "Chat"], read.Sets.Select(static s => s.Name));
        Assert.Equal(Game, Assert.Single(read.Sets[0].Apps));
        Assert.Equal(["example.com"], read.Sets[0].Domains);
    }

    [Fact]
    public async Task Invalid_app_sets_are_refused_with_errors_and_the_saved_ones_stay()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetSetsAsync(client, new AppSetDto("Keep", [Game], []))).Success);

        OperationResponse duplicate = await SetSetsAsync(client, new AppSetDto("A", [], []), new AppSetDto("a", [], []));
        OperationResponse badDomain = await SetSetsAsync(client, new AppSetDto("B", [], ["http://nope"]));

        Assert.False(duplicate.Success);
        Assert.NotEmpty(duplicate.Errors);
        Assert.False(badDomain.Success);
        Assert.Equal("Keep", Assert.Single((await client.InvokeAsync<AppSetsDto>(IpcMethods.AppSetsGet, Ct)).Sets).Name);
    }

    [Fact]
    public async Task App_sets_and_rules_cannot_be_replaced_while_a_session_runs_but_can_be_read()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);

        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await SetSetsAsync(client)));
        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await SetRulesAsync(client)));
        _ = await client.InvokeAsync<AppSetsDto>(IpcMethods.AppSetsGet, Ct);
        _ = await client.InvokeAsync<AppRulesDto>(IpcMethods.AppRulesGet, Ct);
    }

    [Fact]
    public async Task App_rules_are_normalized_saved_and_kept_across_a_restart()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);

        OperationResponse saved = await SetRulesAsync(client, Rule(Daily(45), Range(2, 60, 120), Range(2, 100, 200)));
        await h.RestartAsync();
        IpcClient again = await h.ConnectAsync(IpcCallerRole.User);
        AppRulesDto read = await again.InvokeAsync<AppRulesDto>(IpcMethods.AppRulesGet, Ct);

        Assert.True(saved.Success);
        AppRuleDto rule = Assert.Single(read.Rules);
        Assert.Equal([new WeeklyRangeDto(2, 60, 200)], rule.BlockedRanges);
        Assert.Equal((AppBudgetMode.Daily, 45), (rule.Budget.Mode, rule.Budget.DailyMinutes));
    }

    [Fact]
    public async Task Invalid_app_rules_are_refused_with_errors()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);

        OperationResponse badRange = await SetRulesAsync(client, Rule(null, Range(9, 0, 10)));
        OperationResponse duplicate = await SetRulesAsync(client, Rule(), Rule());

        Assert.False(badRange.Success);
        Assert.NotEmpty(badRange.Errors);
        Assert.False(duplicate.Success);
        Assert.Empty((await client.InvokeAsync<AppRulesDto>(IpcMethods.AppRulesGet, Ct)).Rules);
    }

    [Fact]
    public async Task A_blocked_app_cannot_get_a_looser_rule_through_the_pipe()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(client, Rule(null, [.. Enumerable.Range(0, 7).Select(d => Range(d, 0, 1440))]))).Success);

        OperationResponse removed = await SetRulesAsync(client);
        OperationResponse loosened = await SetRulesAsync(client, Rule(null, Range(0, 0, 60)));

        Assert.False(removed.Success);
        Assert.Contains(removed.Errors, static e => e.Contains("Game", StringComparison.Ordinal));
        Assert.False(loosened.Success);
        Assert.Equal(7, Assert.Single((await client.InvokeAsync<AppRulesDto>(IpcMethods.AppRulesGet, Ct)).Rules).BlockedRanges.Count);
    }

    [Fact]
    public async Task The_status_shows_what_decides_for_every_rule()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        var chat = new AllowlistEntryDto("Chat", ChatPath, null, null);
        var calm = new AllowlistEntryDto("Calm", @"C:\Apps\calm.exe", null, null);
        Assert.True((await SetRulesAsync(client, Rule(null, [.. Enumerable.Range(0, 7).Select(d => Range(d, 0, 1440))]), RuleFor(chat, Daily(0)), RuleFor(calm, Weekly(30)))).Success);

        AppRulesStatusResponse status = await StatusAsync(client);

        Assert.Equal([AppRuleState.ScheduledBlock, AppRuleState.BudgetExhausted, AppRuleState.Allowed], status.Items.Select(static i => i.State));
        Assert.NotNull(status.Items[0].BlockedUntil);
        Assert.NotNull(status.Items[1].BlockedUntil);
        Assert.Equal(1800, status.Items[2].RemainingSeconds);
        Assert.Equal(["Game", "Chat", "Calm"], status.Items.Select(static i => i.App.DisplayName));
    }

    [Fact]
    public async Task During_a_session_every_rule_is_session_controlled()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(client, Rule(Daily(0), Range(0, 0, 1440)))).Success);
        Assert.True((await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)).Success);

        AppRuleStatusDto item = Assert.Single((await StatusAsync(client)).Items);

        Assert.Equal(AppRuleState.SessionControlled, item.State);
        Assert.Null(item.BlockedUntil);
    }

    [Fact]
    public async Task The_stub_may_not_use_the_rule_methods()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient stub = await h.ConnectAsync(IpcCallerRole.Stub);

        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await SetRulesAsync(stub)));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await StatusAsync(stub)));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await SetSetsAsync(stub)));
    }

    [Fact]
    public async Task The_agent_reports_the_foreground_and_the_running_service_enforces_the_pool_without_a_session()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(user, Rule(Daily(1)))).Success);
        h.Monitor.Running.Add(OrchestrationHarness.P(600, GamePath));
        h.Controller.Images[600] = GamePath;
        IpcClient agent = await ConnectAgentAsync(h);
        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 600_000, GamePath), Ct);

        await h.AdvanceUntilAsync(() => h.Controller.Terminated.Count > 0);

        AppRuleStatusDto status = Assert.Single((await StatusAsync(user)).Items);
        Assert.Equal(AppRuleState.BudgetExhausted, status.State);
        Assert.True(status.UsedSeconds >= 60);
        Assert.Equal([(600, 600_000L)], h.Controller.Terminated);
        Assert.Single(h.Launcher.Launches);
    }

    [Fact]
    public async Task An_idle_report_pauses_the_metering_and_the_usage_survives_a_restart()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(user, Rule(Daily(60)))).Success);
        h.Controller.Images[600] = GamePath;
        IpcClient agent = await ConnectAgentAsync(h);
        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 600_000, GamePath), Ct);
        await h.AdvanceUntilAsync(() => h.App.AppRules.GetStatus(false).Items[0].UsedSeconds >= 10);

        _ = await agent.InvokeAsync<IdleChangedDto, EmptyDto>(IpcMethods.AgentIdleChanged, new IdleChangedDto(true, 300), Ct);
        long before = h.App.AppRules.GetStatus(false).Items[0].UsedSeconds;
        h.Time.Advance(TimeSpan.FromSeconds(20));
        await h.App.AppEnforcer.TickOnceAsync(Ct);
        await h.RestartAsync();

        long after = h.App.AppRules.GetStatus(false).Items[0].UsedSeconds;
        Assert.True(before >= 10);
        Assert.Equal(before, after); // twenty idle seconds were not counted, and what was counted survived the restart
    }

    [Fact]
    public async Task Foreground_and_idle_reports_of_a_peer_that_never_registered_as_the_agent_are_ignored()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(user, Rule(Daily(60)))).Success);
        h.Controller.Images[600] = GamePath;
        h.Controller.Images[700] = UnrelatedPath;
        IpcClient agent = await ConnectAgentAsync(h);
        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 600_000, GamePath), Ct);
        await h.AdvanceUntilAsync(() => h.App.AppRules.GetStatus(false).Items[0].UsedSeconds >= 5);

        // A second process with the Agent role that skipped agent.register: it must neither move the foreground nor pause the metering.
        IpcClient impostor = await h.ConnectAsync(IpcCallerRole.Agent);
        _ = await impostor.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(700, 700_000, UnrelatedPath), Ct);
        _ = await impostor.InvokeAsync<IdleChangedDto, EmptyDto>(IpcMethods.AgentIdleChanged, new IdleChangedDto(true, 300), Ct);
        long before = h.App.AppRules.GetStatus(false).Items[0].UsedSeconds;

        await h.AdvanceUntilAsync(() => h.App.AppRules.GetStatus(false).Items[0].UsedSeconds >= before + 5);
        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 600_000, GamePath), Ct);

        Assert.DoesNotContain(h.Plugins.Events.OfType<ActiveWindowChanged>(), static e => e.Window.ProcessId == 700);
        Assert.DoesNotContain(h.Plugins.Events.OfType<UserIdleChanged>(), static e => e.IsIdle);
    }

    [Fact]
    public async Task The_foreground_image_is_the_one_the_system_reports_for_the_process_not_the_one_the_agent_sends()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(user, Rule(Daily(60)))).Success);
        h.Controller.Images[600] = GamePath;
        IpcClient agent = await ConnectAgentAsync(h);

        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 600_000, UnrelatedPath), Ct);

        await h.AdvanceUntilAsync(() => h.App.AppRules.GetStatus(false).Items[0].UsedSeconds >= 5);
        ActiveWindowChanged seen = Assert.IsType<ActiveWindowChanged>(await h.Plugins.WaitForEventAsync(static e => e is ActiveWindowChanged));
        Assert.Equal(GamePath, seen.Window.ExecutablePath);
        Assert.Equal("game.exe", seen.Window.ImageName);
    }

    [Fact]
    public async Task A_foreground_report_for_a_process_the_system_cannot_name_is_ignored()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await SetRulesAsync(user, Rule(Daily(60)))).Success);
        h.Controller.Images[600] = GamePath;
        IpcClient agent = await ConnectAgentAsync(h);
        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(600, 600_000, GamePath), Ct);
        await h.AdvanceUntilAsync(() => h.App.AppRules.GetStatus(false).Items[0].UsedSeconds >= 5);

        _ = await agent.InvokeAsync<ForegroundWindowDto, EmptyDto>(IpcMethods.AgentForegroundWindow, new ForegroundWindowDto(999, 999_000, UnrelatedPath), Ct);
        long before = h.App.AppRules.GetStatus(false).Items[0].UsedSeconds;

        await h.AdvanceUntilAsync(() => h.App.AppRules.GetStatus(false).Items[0].UsedSeconds >= before + 5); // still the game that is counted
        Assert.DoesNotContain(h.Plugins.Events.OfType<ActiveWindowChanged>(), static e => e.Window.ProcessId == 999);
    }

    private static async Task<IpcClient> ConnectAgentAsync(ServiceHostHarness h)
    {
        IpcClient agent = await h.ConnectAsync(IpcCallerRole.Agent);
        AgentRegisterResponse response = await agent.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(AgentProtocol.Version, 1), Ct);
        Assert.True(response.Accepted);
        return agent;
    }
}
