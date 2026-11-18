using System.IO.Pipes;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.Ipc;

public sealed class IpcServerClientTests
{
    private const IpcCallerRole UserRole = IpcCallerRole.User;
    private const IpcCallerRole AdminRole = IpcCallerRole.User | IpcCallerRole.Administrator;
    private const string EchoMethod = "plugin.com.example.echo.echo";
    private const string WaitMethod = "plugin.com.example.echo.wait";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static Task<T> Within<T>(Task<T> task) => task.WaitAsync(Patience, Ct);

    private static Task Within(Task task) => task.WaitAsync(Patience, Ct);

    private static ValueTask<SessionStatusDto> IdleStatus(IpcRequestContext context, EmptyDto request, CancellationToken ct) =>
        ValueTask.FromResult(IpcHarness.Status());

    private static async Task<IpcErrorCode> CodeOf(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<IpcException>(call);
        return ex.Code;
    }

    private static async Task<NamedPipeClientStream> RawConnectAsync(string pipeName)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(5000, Ct);
        return stream;
    }

    private static async Task<IpcEnvelope> ReadEnvelopeAsync(Stream stream)
    {
        var frame = await FrameCodec.ReadFrameAsync(stream, IpcLimits.MaxMessageBytes, Ct);
        return JsonSerializer.Deserialize(frame!, IpcJsonContext.Default.IpcEnvelope)!;
    }

    // ---- request / response ----

    [Fact]
    public async Task Typed_request_receives_typed_response()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var client = h.NewClient();

        var status = await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        Assert.Equal(IpcHarness.Status(), status);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Handler_sees_the_caller_identity_and_method()
    {
        await using var h = new IpcHarness(UserRole).Started();
        IpcRequestContext? seen = null;
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, (context, _, _) =>
        {
            seen = context;
            return ValueTask.FromResult(IpcHarness.Status());
        });

        await h.NewClient().InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        Assert.Equal(UserRole, seen?.Identity.Roles);
        Assert.Equal(4242, seen?.Identity.ProcessId);
        Assert.Equal(IpcMethods.SessionGetStatus, seen?.Method);
    }

    [Fact]
    public async Task Unknown_method_and_method_without_handler_are_not_found()
    {
        await using var h = new IpcHarness(UserRole).Started();
        var client = h.NewClient();

        Assert.Equal(IpcErrorCode.NotFound, await CodeOf(async () => await client.InvokeAsync("does.notExist", null, Ct)));
        Assert.Equal(IpcErrorCode.NotFound, await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionGetStatus, null, Ct)));
    }

    [Fact]
    public async Task Malformed_parameters_of_a_typed_handler_are_an_invalid_request()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, (_, _, _) => ValueTask.FromResult(new BreakGlassResponse(false)));
        var client = h.NewClient();

        var wrongType = await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionBreakGlass, IpcHarness.Json("""{"code":5}"""), Ct));
        var unknownMember = await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionBreakGlass, IpcHarness.Json("""{"code":"x","admin":true}"""), Ct));

        Assert.Equal(IpcErrorCode.InvalidRequest, wrongType);
        Assert.Equal(IpcErrorCode.InvalidRequest, unknownMember);
    }

    [Fact]
    public async Task Handler_can_return_a_deliberate_validation_error()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register(IpcMethods.SettingsGet, (_, _, _) => throw new IpcException(IpcErrorCode.ValidationFailed, "Unknown setting."));

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await h.NewClient().InvokeAsync(IpcMethods.SettingsGet, null, Ct));

        Assert.Equal(IpcErrorCode.ValidationFailed, ex.Code);
        Assert.Equal("Unknown setting.", ex.Message);
    }

    [Fact]
    public async Task Registration_validates_and_can_be_removed()
    {
        await using var h = new IpcHarness(UserRole).Started();
        using var registration = h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);

        Assert.Throws<ArgumentException>(() => h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus));
        Assert.Throws<ArgumentException>(() => h.Server.Register("made.up", (_, _, _) => ValueTask.FromResult(default(JsonElement))));

        registration.Dispose();
        var client = h.NewClient();
        Assert.Equal(IpcErrorCode.NotFound, await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionGetStatus, null, Ct)));
    }

    // ---- authorization and gates ----

    [Fact]
    public async Task Caller_without_the_required_role_is_unauthorized_and_the_handler_does_not_run()
    {
        await using var h = new IpcHarness(IpcCallerRole.Agent).Started();
        var invoked = false;
        h.Server.Register<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, (_, _, _) =>
        {
            invoked = true;
            return ValueTask.FromResult(new StartSessionResponse(true, [], null, null));
        });
        var request = new StartSessionRequest(60, 1, 5, false, [], []);

        var code = await CodeOf(async () => await h.NewClient().InvokeAsync<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, request, Ct));

        Assert.Equal(IpcErrorCode.Unauthorized, code);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Plugin_install_requires_the_administrator_role()
    {
        await using var h = new IpcHarness(UserRole).Started();
        var client = h.NewClient();

        var code = await CodeOf(async () => await client.InvokeAsync<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, new InstallPluginRequest("AAAA"), Ct));

        Assert.Equal(IpcErrorCode.Unauthorized, code);
    }

    [Fact]
    public async Task Server_to_client_methods_cannot_be_called_by_a_client()
    {
        await using var h = new IpcHarness(AdminRole | IpcCallerRole.Agent | IpcCallerRole.Stub).Started();
        var client = h.NewClient();

        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOf(async () => await client.InvokeAsync(IpcMethods.AgentCloseProcess, null, Ct)));
        Assert.Equal(IpcErrorCode.Unauthorized, await CodeOf(async () => await client.InvokeAsync(IpcMethods.EventSessionChanged, null, Ct)));
    }

    [Fact]
    public async Task Mutating_methods_are_rejected_while_a_session_runs_and_work_afterwards()
    {
        await using var h = new IpcHarness(UserRole).Started();
        var invocations = 0;
        h.Server.Register<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, (_, _, _) =>
        {
            invocations++;
            return ValueTask.FromResult(new OperationResponse(true, []));
        });
        var client = h.NewClient();
        var allowlist = new AllowlistDto([], ["example.com"]);

        h.SessionActive = true;
        var code = await CodeOf(async () => await client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, allowlist, Ct));
        h.SessionActive = false;
        var ok = await client.InvokeAsync<AllowlistDto, OperationResponse>(IpcMethods.AllowlistSet, allowlist, Ct);

        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, code);
        Assert.True(ok.Success);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task Plugin_install_by_an_administrator_is_still_rejected_while_a_session_runs()
    {
        await using var h = new IpcHarness(AdminRole).Started();
        h.SessionActive = true;

        var code = await CodeOf(async () => await h.NewClient().InvokeAsync<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, new InstallPluginRequest("AAAA"), Ct));

        Assert.Equal(IpcErrorCode.NotAllowedDuringSession, code);
    }

    [Fact]
    public async Task Session_control_methods_remain_available_during_a_session()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        h.SessionActive = true;

        var status = await h.NewClient().InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        Assert.Equal(IpcHarness.Status(), status);
    }

    [Fact]
    public async Task Connection_is_refused_when_the_resolver_returns_no_identity()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Identity.Refuse = true;

        await Assert.ThrowsAnyAsync<IOException>(async () => await h.NewClient().InvokeAsync(IpcMethods.SessionGetStatus, null, Ct));
    }

    // ---- payload limits ----

    [Fact]
    public async Task Request_above_the_method_limit_is_rejected_and_the_connection_survives()
    {
        await using var h = new IpcHarness(AdminRole).Started();
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var client = h.NewClient();
        var big = IpcHarness.Json($$"""{"pad":"{{new string('a', 300_000)}}"}""");

        var code = await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionGetStatus, big, Ct));
        var status = await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        Assert.Equal(IpcErrorCode.PayloadTooLarge, code);
        Assert.Equal(IpcHarness.Status(), status);
    }

    [Fact]
    public async Task Frame_above_the_roles_limit_is_refused_from_its_header_and_the_connection_closes()
    {
        await using var h = new IpcHarness(UserRole).Started();
        await using var raw = await RawConnectAsync(h.PipeName);

        // Announce 1 MiB (a User may send at most 256 KiB) but send no payload: the server must answer from the header alone.
        await raw.WriteAsync(new byte[] { 0x00, 0x00, 0x10, 0x00 }, Ct);
        var error = await ReadEnvelopeAsync(raw);
        var next = await FrameCodec.ReadFrameAsync(raw, IpcLimits.MaxMessageBytes, Ct);

        Assert.Null(error.Id);
        Assert.Equal(IpcErrorCode.PayloadTooLarge, error.Error?.Code);
        Assert.Null(next);
    }

    [Fact]
    public async Task Administrator_may_send_a_plugin_package_up_to_the_global_limit()
    {
        await using var h = new IpcHarness(AdminRole).Started();
        var received = 0;
        h.Server.Register<InstallPluginRequest, InstallPluginResponse>(IpcMethods.PluginsInstall, (_, request, _) =>
        {
            received = request.PackageBase64.Length;
            return ValueTask.FromResult(new InstallPluginResponse(true, "com.example.big", []));
        });

        var response = await h.NewClient().InvokeAsync<InstallPluginRequest, InstallPluginResponse>(
            IpcMethods.PluginsInstall,
            new InstallPluginRequest(new string('A', 3_000_000)),
            Ct);

        Assert.True(response.Success);
        Assert.Equal(3_000_000, received);
    }

    [Fact]
    public async Task Response_larger_than_the_global_limit_becomes_a_generic_internal_error()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register<EmptyDto, DownloadTemplateResponse>(IpcMethods.PluginsDownloadTemplate, (_, _, _) =>
            ValueTask.FromResult(new DownloadTemplateResponse("t.zip", new string('A', IpcLimits.MaxMessageBytes))));

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await h.NewClient().InvokeAsync(IpcMethods.PluginsDownloadTemplate, null, Ct));

        Assert.Equal(IpcErrorCode.Internal, ex.Code);
        Assert.Equal("Internal error.", ex.Message);
    }

    [Fact]
    public async Task Malformed_json_gets_an_invalid_request_error_and_the_connection_closes()
    {
        await using var h = new IpcHarness(UserRole).Started();
        await using var raw = await RawConnectAsync(h.PipeName);

        await FrameCodec.WriteFrameAsync(raw, "{ not json"u8.ToArray(), 1024, Ct);
        var error = await ReadEnvelopeAsync(raw);
        var next = await FrameCodec.ReadFrameAsync(raw, IpcLimits.MaxMessageBytes, Ct);

        Assert.Equal(IpcErrorCode.InvalidRequest, error.Error?.Code);
        Assert.Null(next);
    }

    [Fact]
    public async Task Envelope_with_unknown_members_is_a_protocol_violation()
    {
        await using var h = new IpcHarness(UserRole).Started();
        await using var raw = await RawConnectAsync(h.PipeName);

        await FrameCodec.WriteFrameAsync(raw, """{"id":1,"method":"session.getStatus","role":"Administrator"}"""u8.ToArray(), 1024, Ct);
        var error = await ReadEnvelopeAsync(raw);

        Assert.Equal(IpcErrorCode.InvalidRequest, error.Error?.Code);
    }

    // ---- rate limiting ----

    [Fact]
    public async Task Requests_beyond_the_burst_are_rate_limited_until_tokens_refill()
    {
        var time = new FakeTimeProvider();
        await using var h = new IpcHarness(UserRole, new IpcServerOptions { RequestsPerSecond = 1, RateBurst = 5 }, time).Started();
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var client = h.NewClient();

        for (var i = 0; i < 5; i++)
        {
            await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        }

        var limited = await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionGetStatus, null, Ct));
        time.Advance(TimeSpan.FromSeconds(2));
        await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        var limitedAgain = await CodeOf(async () => await client.InvokeAsync(IpcMethods.SessionGetStatus, null, Ct));

        Assert.Equal(IpcErrorCode.RateLimited, limited);
        Assert.Equal(IpcErrorCode.RateLimited, limitedAgain);
    }

    [Fact]
    public async Task Rate_limit_is_per_connection()
    {
        var time = new FakeTimeProvider();
        await using var h = new IpcHarness(UserRole, new IpcServerOptions { RequestsPerSecond = 1, RateBurst = 2 }, time).Started();
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var noisy = h.NewClient();
        var quiet = h.NewClient();
        await noisy.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        await noisy.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        Assert.Equal(IpcErrorCode.RateLimited, await CodeOf(async () => await noisy.InvokeAsync(IpcMethods.SessionGetStatus, null, Ct)));

        Assert.Equal(IpcHarness.Status(), await quiet.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct));
    }

    [Fact]
    public async Task Connections_beyond_the_maximum_are_dropped()
    {
        await using var h = new IpcHarness(UserRole, new IpcServerOptions { MaxConnections = 2 }).Started();
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var first = h.NewClient();
        var second = h.NewClient();
        await first.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        await second.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        await Assert.ThrowsAnyAsync<IOException>(async () => await h.NewClient().InvokeAsync(IpcMethods.SessionGetStatus, null, Ct));

        Assert.Equal(2, h.Server.Peers.Count);
    }

    // ---- errors never leak details ----

    [Fact]
    public async Task Handler_exception_becomes_a_generic_internal_error_and_details_are_only_logged()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register(IpcMethods.SettingsGet, (_, _, _) => throw new InvalidOperationException("secret-detail C:\\internal\\path"));

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await h.NewClient().InvokeAsync(IpcMethods.SettingsGet, null, Ct));

        Assert.Equal(IpcErrorCode.Internal, ex.Code);
        Assert.Equal("Internal error.", ex.Message);
        Assert.DoesNotContain("secret-detail", ex.ToString(), StringComparison.Ordinal);
        Assert.Contains(h.Logger.Entries, e => e.Exception?.Message.Contains("secret-detail", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Server_keeps_serving_after_a_handler_failure()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register(IpcMethods.SettingsGet, (_, _, _) => throw new InvalidOperationException("boom"));
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var client = h.NewClient();

        Assert.Equal(IpcErrorCode.Internal, await CodeOf(async () => await client.InvokeAsync(IpcMethods.SettingsGet, null, Ct)));

        Assert.Equal(IpcHarness.Status(), await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct));
    }

    // ---- concurrency ----

    [Fact]
    public async Task Concurrent_requests_are_correlated_to_their_own_responses()
    {
        const int count = 12;
        await using var h = new IpcHarness(UserRole).Started();
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Register(EchoMethod, async (_, parameters, ct) =>
        {
            // No handler answers before all of them are in flight, so responses cannot be matched by arrival order.
            if (Interlocked.Increment(ref arrived) == count)
            {
                allArrived.SetResult();
            }

            await allArrived.Task.WaitAsync(ct);
            return parameters;
        });
        var client = h.NewClient();

        var calls = Enumerable.Range(0, count)
            .Select(i => client.InvokeAsync(EchoMethod, IpcHarness.Json($$"""{"n":{{i}}}"""), Ct).AsTask())
            .ToArray();
        var results = await Within(Task.WhenAll(calls));

        Assert.Equal(Enumerable.Range(0, count), results.Select(r => r.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task Requests_beyond_the_per_connection_in_flight_limit_are_rejected_immediately()
    {
        const int sent = IpcConnection.MaxInFlightRequests + 4;
        await using var h = new IpcHarness(UserRole).Started();
        var arrived = 0;
        var limitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Register(WaitMethod, async (_, parameters, ct) =>
        {
            if (Interlocked.Increment(ref arrived) == IpcConnection.MaxInFlightRequests)
            {
                limitReached.SetResult();
            }

            await release.Task.WaitAsync(ct);
            return parameters;
        });
        var client = h.NewClient();
        var pending = Enumerable.Range(0, sent).Select(_ => client.InvokeAsync(WaitMethod, null, Ct).AsTask()).ToList();

        await Within(limitReached.Task);
        var rejected = new List<IpcErrorCode>();
        for (var i = 0; i < 4; i++)
        {
            var done = await Within(Task.WhenAny(pending));
            pending.Remove(done);
            rejected.Add((await Assert.ThrowsAsync<IpcException>(() => done)).Code);
        }

        release.SetResult();
        await Within(Task.WhenAll(pending));

        Assert.All(rejected, code => Assert.Equal(IpcErrorCode.RateLimited, code));
        Assert.Equal(IpcConnection.MaxInFlightRequests, pending.Count);
    }

    // ---- notifications and server-initiated requests ----

    [Fact]
    public async Task Subscribed_clients_receive_pushed_notifications_and_others_do_not()
    {
        await using var h = new IpcHarness(UserRole).Started();
        h.Server.Register<EmptyDto, SubscribeResponse>(IpcMethods.EventsSubscribe, (_, _, _) => ValueTask.FromResult(new SubscribeResponse(IpcHarness.Status())));
        h.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        var subscriber = h.NewClient();
        var bystander = h.NewClient();
        var received = new TaskCompletionSource<IpcNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bystanderCount = 0;
        subscriber.NotificationReceived += (_, e) => received.TrySetResult(e);
        bystander.NotificationReceived += (_, _) => Interlocked.Increment(ref bystanderCount);
        var subscription = await subscriber.InvokeAsync<SubscribeResponse>(IpcMethods.EventsSubscribe, Ct);
        await bystander.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        var running = IpcHarness.Status() with { Phase = Blocker.Sdk.Session.SessionPhase.Active, RemainingSeconds = 600 };
        await h.Server.PublishAsync(IpcMethods.EventSessionChanged, running, Ct);
        var notification = await Within(received.Task);
        await bystander.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);

        Assert.Equal(IpcHarness.Status(), subscription.Status);
        Assert.Equal(IpcMethods.EventSessionChanged, notification.Method);
        Assert.Equal(running, IpcJson.FromElement<SessionStatusDto>(notification.Parameters));
        Assert.Equal(0, bystanderCount);
    }

    [Fact]
    public async Task Subscribing_without_a_custom_handler_still_subscribes()
    {
        await using var h = new IpcHarness(UserRole).Started();
        var client = h.NewClient();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, e) => received.TrySetResult(e.Method);

        await client.InvokeAsync(IpcMethods.EventsSubscribe, null, Ct);
        await h.Server.PublishAsync(IpcMethods.EventNotification, new NotificationDto("t", "m", NotificationLevel.Info), Ct);

        Assert.Equal(IpcMethods.EventNotification, await Within(received.Task));
    }

    [Fact]
    public async Task Server_can_send_a_request_to_a_registered_agent_and_get_its_answer()
    {
        await using var h = new IpcHarness(UserRole | IpcCallerRole.Agent).Started();
        var agent = new TaskCompletionSource<IpcPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Register<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, (context, _, _) =>
        {
            agent.SetResult(context.Peer);
            return ValueTask.FromResult(new AgentRegisterResponse(true, 1));
        });
        var client = h.NewClient();
        client.Register(IpcMethods.AgentCloseProcess, (parameters, _) =>
        {
            var request = IpcJson.FromElement<CloseProcessRequest>(parameters);
            return ValueTask.FromResult(IpcJson.ToElement(new CloseProcessResponse(request.ProcessId == 77)));
        });
        await client.InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(1, 2), Ct);

        var peer = await Within(agent.Task);
        var answer = await peer.InvokeAsync(IpcMethods.AgentCloseProcess, IpcJson.ToElement(new CloseProcessRequest(77, 5, 1000)), TimeSpan.FromSeconds(10), Ct);

        Assert.True(IpcJson.FromElement<CloseProcessResponse>(answer).Closed);
    }

    [Fact]
    public async Task Client_answers_unknown_server_requests_with_not_found()
    {
        await using var h = new IpcHarness(UserRole | IpcCallerRole.Agent).Started();
        var agent = new TaskCompletionSource<IpcPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Register<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, (context, _, _) =>
        {
            agent.SetResult(context.Peer);
            return ValueTask.FromResult(new AgentRegisterResponse(true, 1));
        });
        await h.NewClient().InvokeAsync<AgentRegisterRequest, AgentRegisterResponse>(IpcMethods.AgentRegister, new AgentRegisterRequest(1, 2), Ct);
        var peer = await Within(agent.Task);

        var ex = await Assert.ThrowsAsync<IpcException>(async () => await peer.InvokeAsync(IpcMethods.AgentNotify, null, TimeSpan.FromSeconds(10), Ct));

        Assert.Equal(IpcErrorCode.NotFound, ex.Code);
    }

    // ---- timeouts, shutdown, reconnect ----

    [Fact]
    public async Task Request_without_a_response_times_out()
    {
        var clientTime = new FakeTimeProvider();
        await using var h = new IpcHarness(UserRole).Started();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Register(EchoMethod, async (_, _, ct) =>
        {
            started.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(ct);
            return default;
        });
        var client = h.NewClient(new IpcClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) }, clientTime);

        var call = client.InvokeAsync(EchoMethod, null, Ct).AsTask();
        await Within(started.Task);
        clientTime.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Server_shutdown_cancels_handlers_and_fails_pending_calls()
    {
        var h = new IpcHarness(UserRole).Started();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.Register(EchoMethod, async (_, _, ct) =>
        {
            started.SetResult();
            try
            {
                await new TaskCompletionSource().Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return default;
        });
        var client = h.NewClient();
        var call = client.InvokeAsync(EchoMethod, null, Ct).AsTask();
        await Within(started.Task);

        await h.Server.DisposeAsync();

        await Assert.ThrowsAnyAsync<IOException>(() => call);
        await Within(cancelled.Task);
        await Within(h.Server.Completion);
        Assert.Empty(h.Server.Peers);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Run_reconnects_after_the_server_restarts()
    {
        var pipeName = $"blk-{Guid.NewGuid():N}";
        var first = new IpcHarness(UserRole, pipeName: pipeName).Started();
        await using var client = new IpcClient(
            pipeName,
            NullLogger<IpcClient>.Instance,
            new IpcClientOptions { ReconnectDelayMin = TimeSpan.FromMilliseconds(1), ReconnectDelayMax = TimeSpan.FromMilliseconds(5), ConnectTimeout = TimeSpan.FromSeconds(10) });
        var connects = 0;
        var firstConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = client.RunAsync(
            _ =>
            {
                (Interlocked.Increment(ref connects) == 1 ? firstConnect : secondConnect).TrySetResult();
                return Task.CompletedTask;
            },
            stop.Token);
        await Within(firstConnect.Task);

        await first.DisposeAsync();
        await using var second = new IpcHarness(UserRole, pipeName: pipeName).Started();
        second.Server.Register<EmptyDto, SessionStatusDto>(IpcMethods.SessionGetStatus, IdleStatus);
        await Within(secondConnect.Task);
        var status = await client.InvokeAsync<SessionStatusDto>(IpcMethods.SessionGetStatus, Ct);
        await stop.CancelAsync();
        await loop;

        Assert.Equal(IpcHarness.Status(), status);
        Assert.Equal(2, connects);
    }

    [Fact]
    public async Task A_second_server_cannot_take_over_a_pipe_name_that_is_in_use()
    {
        await using var h = new IpcHarness(UserRole).Started();
        await using var impostor = new IpcServer(h.PipeName, new DefaultPipeListenerFactory(), h.Identity, () => false, NullLogger<IpcServer>.Instance);

        // Windows and Unix both report a name that already has a first instance as access denied.
        var ex = Assert.ThrowsAny<Exception>(impostor.Start);

        Assert.True(ex is UnauthorizedAccessException or IOException, ex.GetType().Name);
    }
}
