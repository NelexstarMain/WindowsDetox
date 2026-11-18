using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.Ports;
using Blocker.Core.ServiceHost;
using Blocker.Core.Tests.Orchestration;
using Blocker.Core.Tests.Plugins;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class ServiceHandlerTests
{
    private const IpcCallerRole Admin = IpcCallerRole.User | IpcCallerRole.Administrator;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IpcErrorCode> CodeOfAsync(Func<ValueTask> call)
    {
        IpcException error = await Assert.ThrowsAsync<IpcException>(async () => await call());
        return error.Code;
    }

    private static string Package() => Convert.ToBase64String(PluginTestSupport.ZipWithManifest(("Plugin.cs", "// plugin")).ToArray());

    private static async Task<StartSessionResponse> StartAsync(IpcClient client, StartSessionRequest? request = null) =>
        await client.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, request ?? ServiceHostHarness.Request(), Ct);

    private static ValueTask<InstallPluginResponse> InstallAsync(IpcClient client, string base64) =>
        client.InvokeAsync<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, new InstallPluginRequest(base64), Ct);

    // ---- roles and the session gate ----

    [Fact]
    public async Task Installing_a_plugin_needs_the_administrator_role()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);

        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await InstallAsync(user, Package())));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await user.InvokeAsync<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, new RemovePluginRequest("com.test.sample"), Ct)));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await user.InvokeAsync<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, new SetPluginEnabledRequest("com.test.sample", false), Ct)));
        Assert.Empty(h.Plugins.Invocations);
    }

    [Fact]
    public async Task Methods_that_change_the_setup_are_refused_while_a_session_runs()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient admin = await h.ConnectAsync(Admin);
        Assert.True((await StartAsync(admin)).Success);

        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await admin.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, new AllowlistDto([], []), Ct)));
        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await admin.InvokeAsync<SettingsDto, OperationResponse>(IpcMethods.SettingsSet, new SettingsDto(new Dictionary<string, string> { ["effects.quality"] = "low" }), Ct)));
        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await InstallAsync(admin, Package())));
        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await admin.InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, ServiceHostHarness.Request(), Ct)));
        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, await CodeOfAsync(async () => await admin.InvokeAsync<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, new RemovePluginRequest("com.test.sample"), Ct)));
    }

    [Fact]
    public async Task Developer_abort_does_nothing_unless_the_service_runs_in_dev_mode()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(devMode: false);
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await StartAsync(client)).Success);

        DevAbortResponse response = await client.InvokeAsync<DevAbortResponse>(IpcMethods.SessionDevAbort, Ct);

        Assert.False(response.Aborted);
        Assert.Equal(SessionPhase.Active, h.App.Snapshot.Phase);
        Assert.NotEmpty(await h.JournalAsync());
    }

    [Fact]
    public async Task Developer_abort_ends_the_session_in_dev_mode_and_rolls_everything_back()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync(devMode: true);
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await StartAsync(client)).Success);

        DevAbortResponse response = await client.InvokeAsync<DevAbortResponse>(IpcMethods.SessionDevAbort, Ct);

        Assert.True(response.Aborted);
        Assert.Equal(SessionPhase.Idle, h.App.Snapshot.Phase);
        Assert.Empty(await h.JournalAsync());
    }

    [Fact]
    public async Task A_stub_is_not_allowed_to_start_sessions_and_a_user_is_not_allowed_to_act_as_the_agent()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient stub = await h.ConnectAsync(IpcCallerRole.Stub);
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);

        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await StartAsync(stub)));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await user.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(AgentProtocol.Version, 1), Ct)));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOfAsync(async () => await user.InvokeAsync<BlockInfoRequest, BlockInfoResponse>(IpcMethods.StubGetBlockInfo, new BlockInfoRequest("x"), Ct)));
    }

    // ---- session ----

    [Fact]
    public async Task Starting_a_session_validates_the_request_and_never_returns_the_code_twice()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);

        StartSessionResponse invalidDomain = await StartAsync(client, ServiceHostHarness.Request(domains: ["not a domain"]));
        StartSessionResponse tooShort = await StartAsync(client, ServiceHostHarness.Request(minutes: 5));
        StartSessionResponse bad = await StartAsync(client, ServiceHostHarness.Request(apps: [new AllowlistEntryDto("Nothing", null, null, null)]));

        Assert.False(invalidDomain.Success);
        Assert.Contains(invalidDomain.Errors, e => e.Contains("not a domain", StringComparison.Ordinal));
        Assert.False(tooShort.Success);
        Assert.False(bad.Success);
        Assert.All([invalidDomain, tooShort, bad], r => Assert.Null(r.BreakGlassCode));
        Assert.Equal(SessionPhase.Idle, h.App.Snapshot.Phase);
        Assert.Empty(await h.JournalAsync());
    }

    [Fact]
    public async Task A_vetoed_emergency_pass_is_refused_with_the_plugins_reason()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await StartAsync(client)).Success);
        h.Plugins.Locks = action => action == LockAction.UseEmergencyPass ? new EvaluateLockResponse([new LockVetoDto("com.lock.strict", "No passes this week")]) : new EvaluateLockResponse([]);

        IpcException error = await Assert.ThrowsAsync<IpcException>(async () => await client.InvokeAsync<EmptyDto, EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, new EmptyDto(), Ct));

        Assert.Equal(IpcErrorCode.ValidationFailed, error.Code);
        Assert.Contains("No passes this week", error.Message, StringComparison.Ordinal);
        Assert.Equal(SessionPhase.Active, h.App.Snapshot.Phase);
    }

    // ---- allowlist and settings ----

    [Fact]
    public async Task The_draft_allowlist_is_stored_and_used_when_a_session_starts_without_apps()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        var draft = new AllowlistDto([new AllowlistEntryDto("Editor", OrchestrationHarness.Editor, null, null)], ["docs.example.com"]);

        OperationResponse saved = await client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, draft, Ct);
        AllowlistDto read = await client.InvokeAsync<AllowlistDto>(IpcMethods.AllowlistGet, Ct);
        StartSessionResponse started = await StartAsync(client, new StartSessionRequest(60, 1, 5, false, [], []));

        Assert.True(saved.Success);
        Assert.Equal(draft.Apps, read.Apps);
        Assert.Equal(draft.Domains, read.Domains);
        Assert.True(started.Success, string.Join(" ", started.Errors));
        ProcessInfo game = OrchestrationHarness.P(500, OrchestrationHarness.Game);
        ProcessInfo editor = OrchestrationHarness.P(501, OrchestrationHarness.Editor);
        h.Monitor.Spawn(game);
        h.Monitor.Spawn(editor);
        await h.App.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Contains(h.Controller.Terminated, t => t.ProcessId == game.ProcessId);
        Assert.DoesNotContain(h.Controller.Terminated, t => t.ProcessId == editor.ProcessId);

        // While the session runs, allowlist.get shows what is enforced.
        AllowlistDto enforced = await client.InvokeAsync<AllowlistDto>(IpcMethods.AllowlistGet, Ct);
        Assert.Equal(draft.Apps, enforced.Apps);
        Assert.Equal(draft.Domains, enforced.Domains);
    }

    [Fact]
    public async Task An_invalid_allowlist_is_refused_with_reasons_and_not_stored()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);

        OperationResponse noIdentifier = await client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, new AllowlistDto([new AllowlistEntryDto("Ghost", null, null, null)], []), Ct);
        OperationResponse badHash = await client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, new AllowlistDto([new AllowlistEntryDto("Tool", null, "abc", null)], []), Ct);
        OperationResponse badDomain = await client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, new AllowlistDto([], ["http://nope"]), Ct);

        Assert.All([noIdentifier, badHash, badDomain], r =>
        {
            Assert.False(r.Success);
            Assert.NotEmpty(r.Errors);
        });
        Assert.Empty((await client.InvokeAsync<AllowlistDto>(IpcMethods.AllowlistGet, Ct)).Apps);
    }

    [Fact]
    public async Task Settings_have_defaults_validate_their_values_and_persist()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.Equal("high", (await client.InvokeAsync<SettingsDto>(IpcMethods.SettingsGet, Ct)).Values["effects.quality"]);

        OperationResponse ok = await client.InvokeAsync<SettingsDto, OperationResponse>(IpcMethods.SettingsSet, new SettingsDto(new Dictionary<string, string> { ["effects.quality"] = "low" }), Ct);
        OperationResponse unknown = await client.InvokeAsync<SettingsDto, OperationResponse>(IpcMethods.SettingsSet, new SettingsDto(new Dictionary<string, string> { ["nope"] = "1" }), Ct);
        OperationResponse badValue = await client.InvokeAsync<SettingsDto, OperationResponse>(IpcMethods.SettingsSet, new SettingsDto(new Dictionary<string, string> { ["effects.quality"] = "ludicrous" }), Ct);
        await h.RestartAsync();
        IpcClient again = await h.ConnectAsync(IpcCallerRole.User);

        Assert.True(ok.Success);
        Assert.False(unknown.Success);
        Assert.False(badValue.Success);
        Assert.Equal("low", (await again.InvokeAsync<SettingsDto>(IpcMethods.SettingsGet, Ct)).Values["effects.quality"]);
    }

    [Fact]
    public async Task Scanning_apps_returns_the_inventory_and_caches_it_for_the_next_start()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);

        ScanAppsResponse scan = await client.InvokeAsync<ScanAppsResponse>(IpcMethods.AllowlistScanApps, Ct);

        Assert.Equal(["Game", "Editor"], scan.Apps.Select(a => a.DisplayName));
        Assert.Equal(AppSourceDto.StartMenu, scan.Apps[0].Sources);
        Assert.True(h.Fs.File.Exists(h.Paths.AppScanCachePath));
    }

    // ---- plugins ----

    [Fact]
    public async Task A_plugin_is_installed_listed_disabled_and_removed()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient admin = await h.ConnectAsync(Admin);

        InstallPluginResponse installed = await InstallAsync(admin, Package());
        ListPluginsResponse listed = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        OperationResponse disabled = await admin.InvokeAsync<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, new SetPluginEnabledRequest("com.test.sample", false), Ct);
        h.Plugins.Plugins = [new HostPluginStatusDto("com.test.sample", "Sample", "1.2.3", HostPluginState.Faulted, "too slow")];
        ListPluginsResponse afterDisable = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        _ = await admin.InvokeAsync<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, new SetPluginEnabledRequest("com.test.sample", true), Ct);
        ListPluginsResponse afterEnable = await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct);
        OperationResponse removed = await admin.InvokeAsync<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, new RemovePluginRequest("com.test.sample"), Ct);

        Assert.True(installed.Success);
        Assert.Equal("com.test.sample", installed.PluginId);
        Assert.Equal(2, installed.Tests?.Count);
        Assert.Null(installed.FailedStage);
        PluginInfoDto info = Assert.Single(listed.Plugins);
        Assert.Equal(("com.test.sample", "Sample", "1.2.3", "Tester", true, false), (info.Id, info.Name, info.Version, info.Author, info.Enabled, info.Faulted));
        Assert.Equal(["events.session", "tick"], info.Capabilities);
        Assert.True(disabled.Success);
        Assert.False(Assert.Single(afterDisable.Plugins).Enabled);
        Assert.False(Assert.Single(afterDisable.Plugins).Faulted); // a disabled plugin is not running, so it cannot be faulted
        PluginInfoDto enabledAgain = Assert.Single(afterEnable.Plugins);
        Assert.True(enabledAgain.Enabled);
        Assert.True(enabledAgain.Faulted);
        Assert.True(removed.Success);
        Assert.Empty((await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct)).Plugins);
        Assert.True(h.Plugins.Reloads >= 4);
    }

    [Fact]
    public async Task A_plugin_that_fails_the_pipeline_is_not_installed_and_the_report_names_stage_and_tests()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient admin = await h.ConnectAsync(Admin);
        h.Plugins.Evaluate = _ => new PluginInstallReportDto(
            PluginInstallStageDto.Tests,
            ["Failed: Tests.Adds: expected 3 but was 4"],
            "com.test.sample",
            "Sample",
            "1.2.3",
            "Tester",
            [],
            1,
            2,
            [new PluginTestResultDto("Tests.Ok", PluginTestOutcomeDto.Passed), new PluginTestResultDto("Tests.Adds", PluginTestOutcomeDto.Failed, "expected 3 but was 4")]);

        InstallPluginResponse failed = await InstallAsync(admin, Package());
        InstallPluginResponse notBase64 = await InstallAsync(admin, "***not base64***");

        Assert.False(failed.Success);
        Assert.Equal("Tests", failed.FailedStage);
        Assert.Equal(["Failed: Tests.Adds: expected 3 but was 4"], failed.Errors);
        Assert.Equal(new PluginTestResultDto("Tests.Adds", PluginTestOutcomeDto.Failed, "expected 3 but was 4"), failed.Tests![1]);
        Assert.False(notBase64.Success);
        Assert.Equal("Package", notBase64.FailedStage);
        Assert.Empty((await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct)).Plugins);
    }

    [Fact]
    public async Task A_lock_policy_plugin_can_veto_removal_disabling_and_replacement()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient admin = await h.ConnectAsync(Admin);
        Assert.True((await InstallAsync(admin, Package())).Success);
        h.Plugins.Locks = action => new EvaluateLockResponse([new LockVetoDto("com.lock.strict", $"{action} is not allowed")]);

        OperationResponse remove = await admin.InvokeAsync<RemovePluginRequest, OperationResponse>(IpcMethods.PluginsRemove, new RemovePluginRequest("com.test.sample"), Ct);
        OperationResponse disable = await admin.InvokeAsync<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, new SetPluginEnabledRequest("com.test.sample", false), Ct);
        InstallPluginResponse replace = await InstallAsync(admin, Package());
        OperationResponse enable = await admin.InvokeAsync<SetPluginEnabledRequest, OperationResponse>(IpcMethods.PluginsSetEnabled, new SetPluginEnabledRequest("com.test.sample", true), Ct);

        Assert.False(remove.Success);
        Assert.Equal(["Vetoed by com.lock.strict: RemovePlugin is not allowed"], remove.Errors);
        Assert.False(disable.Success);
        Assert.Contains("DisablePlugin", disable.Errors[0], StringComparison.Ordinal);
        Assert.False(replace.Success);
        Assert.Contains("RemovePlugin", replace.Errors[0], StringComparison.Ordinal);
        Assert.True(enable.Success); // enabling is never vetoed
        Assert.True(Assert.Single((await admin.InvokeAsync<ListPluginsResponse>(IpcMethods.PluginsList, Ct)).Plugins).Enabled);
    }

    [Fact]
    public async Task Plugin_methods_ui_contributions_and_actions_go_through_the_plugin_host()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        h.Plugins.OnInvoke = (_, _, payload) => $$"""{"pong":true,"echo":{{payload}}}""";
        h.Plugins.OnAction = request => request.Action.ElementId == "reset" ? new OperationResponse(true, []) : new OperationResponse(false, ["Unknown element."]);

        JsonElement reply = await user.InvokeAsync(IpcMethods.ForPlugin("com.test.sample", "ping"), JsonDocument.Parse("""{"n":1}""").RootElement.Clone(), Ct);
        GetContributionsResponse contributions = await user.InvokeAsync<GetContributionsRequest, GetContributionsResponse>(IpcMethods.UiGetContributions, new GetContributionsRequest(UiSlot.DashboardWidget), Ct);
        _ = await user.InvokeAsync<SendActionRequest, EmptyDto>(IpcMethods.UiSendAction, new SendActionRequest("com.test.sample", "widget-0", UiAction.Click("reset")), Ct);
        IpcException rejected = await Assert.ThrowsAsync<IpcException>(async () =>
            await user.InvokeAsync<SendActionRequest, EmptyDto>(IpcMethods.UiSendAction, new SendActionRequest("com.test.sample", "widget-0", UiAction.Click("other")), Ct));

        Assert.True(reply.GetProperty("pong").GetBoolean());
        Assert.Equal(1, reply.GetProperty("echo").GetProperty("n").GetInt32());
        Assert.Equal(("com.test.sample", "ping"), (h.Plugins.Invocations[0].PluginId, h.Plugins.Invocations[0].Method));
        Assert.Equal("com.test.sample", Assert.Single(contributions.Items).PluginId);
        Assert.Equal(IpcErrorCode.ValidationFailed, rejected.Code);
        Assert.Equal("Unknown element.", rejected.Message);
    }

    [Fact]
    public async Task A_plugin_method_that_returns_garbage_becomes_an_internal_error_without_details()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);
        h.Plugins.OnInvoke = (_, _, _) => "not json";

        IpcException error = await Assert.ThrowsAsync<IpcException>(async () =>
            await user.InvokeAsync(IpcMethods.ForPlugin("com.test.sample", "ping"), JsonDocument.Parse("{}").RootElement.Clone(), Ct));

        Assert.Equal(IpcErrorCode.Internal, error.Code);
        Assert.DoesNotContain("json", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_starter_template_is_offered_as_a_zip_and_unknown_templates_are_refused()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient user = await h.ConnectAsync(IpcCallerRole.User);

        DownloadTemplateResponse template = await user.InvokeAsync<DownloadTemplateRequest, DownloadTemplateResponse>(IpcMethods.PluginsDownloadTemplate, new DownloadTemplateRequest(null), Ct);
        IpcException unknown = await Assert.ThrowsAsync<IpcException>(async () =>
            await user.InvokeAsync<DownloadTemplateRequest, DownloadTemplateResponse>(IpcMethods.PluginsDownloadTemplate, new DownloadTemplateRequest("evil"), Ct));

        Assert.EndsWith(".zip", template.FileName, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4], Convert.FromBase64String(template.PackageBase64));
        Assert.Equal(IpcErrorCode.ValidationFailed, unknown.Code);
    }

    // ---- stub and agent ----

    [Fact]
    public async Task The_block_screen_learns_the_file_name_and_the_session_state_and_nothing_else()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient stub = await h.ConnectAsync(IpcCallerRole.Stub);

        BlockInfoResponse info = await stub.InvokeAsync<BlockInfoRequest, BlockInfoResponse>(IpcMethods.StubGetBlockInfo, new BlockInfoRequest(@"C:\Games\game.exe"), Ct);
        BlockInfoResponse dirty = await stub.InvokeAsync<BlockInfoRequest, BlockInfoResponse>(IpcMethods.StubGetBlockInfo, new BlockInfoRequest("C:\\x\\evil\u0007\n.exe"), Ct);
        BlockInfoResponse none = await stub.InvokeAsync<BlockInfoRequest, BlockInfoResponse>(IpcMethods.StubGetBlockInfo, new BlockInfoRequest(string.Empty), Ct);

        Assert.Equal("game.exe", info.Subject);
        Assert.Equal(SessionPhase.Idle, info.Status.Phase);
        Assert.Equal("evil??.exe", dirty.Subject);
        Assert.Equal("an application", none.Subject);
    }

    [Fact]
    public async Task An_agent_with_another_protocol_version_is_not_accepted()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient agent = await h.ConnectAsync(IpcCallerRole.Agent);

        AgentRegisterResponse response = await agent.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(99, 1), Ct);

        Assert.False(response.Accepted);
        Assert.Equal(AgentProtocol.Version, response.ProtocolVersion);
    }

    [Fact]
    public async Task Subscribing_returns_the_current_status()
    {
        await using ServiceHostHarness h = await ServiceHostHarness.StartAsync();
        IpcClient client = await h.ConnectAsync(IpcCallerRole.User);
        Assert.True((await StartAsync(client)).Success);

        SubscribeResponse subscription = await client.InvokeAsync<SubscribeResponse>(IpcMethods.EventsSubscribe, Ct);

        Assert.Equal(SessionPhase.Active, subscription.Status.Phase);
    }
}
