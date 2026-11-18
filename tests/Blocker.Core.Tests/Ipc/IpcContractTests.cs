using System.Reflection;
using System.Text;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Sdk;

namespace Blocker.Core.Tests.Ipc;

public sealed class IpcContractTests
{
    private static IEnumerable<string> AllMethodConstants() =>
        typeof(IpcMethods)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: "String" } && f.Name != nameof(IpcMethods.PluginPrefix))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_method_constant_has_a_policy_rule()
    {
        foreach (var method in AllMethodConstants())
        {
            Assert.True(IpcMethodPolicy.TryGet(method, out _), method);
        }

        Assert.Equal(AllMethodConstants().Count(), IpcMethodPolicy.All.Count);
    }

    [Fact]
    public void Plugin_install_is_administrator_only_large_and_blocked_during_a_session()
    {
        Assert.True(IpcMethodPolicy.TryGet(IpcMethods.PluginsInstall, out var rule));

        Assert.Equal(IpcCallerRole.Administrator, rule.AllowedRoles);
        Assert.Equal(4 * 1024 * 1024, rule.MaxPayloadBytes);
        Assert.False(rule.AllowedDuringSession);
        Assert.False(rule.Allows(IpcCallerRole.User));
    }

    [Theory]
    [InlineData(IpcMethods.AllowlistSet)]
    [InlineData(IpcMethods.SettingsSet)]
    [InlineData(IpcMethods.SessionStart)]
    [InlineData(IpcMethods.PluginsRemove)]
    [InlineData(IpcMethods.PluginsSetEnabled)]
    public void Mutating_methods_are_rejected_during_a_session(string method)
    {
        Assert.True(IpcMethodPolicy.TryGet(method, out var rule));

        Assert.False(rule.AllowedDuringSession);
    }

    [Theory]
    [InlineData(IpcMethods.SessionGetStatus)]
    [InlineData(IpcMethods.SessionUseEmergencyPass)]
    [InlineData(IpcMethods.SessionBreakGlass)]
    [InlineData(IpcMethods.EventsSubscribe)]
    public void Session_control_methods_work_during_a_session(string method)
    {
        Assert.True(IpcMethodPolicy.TryGet(method, out var rule));

        Assert.True(rule.AllowedDuringSession);
    }

    [Theory]
    [InlineData(IpcMethods.AgentCloseProcess)]
    [InlineData(IpcMethods.AgentNotify)]
    [InlineData(IpcMethods.EventSessionChanged)]
    [InlineData(IpcMethods.EventNotification)]
    public void Server_to_client_methods_cannot_be_called_by_any_role(string method)
    {
        Assert.True(IpcMethodPolicy.TryGet(method, out var rule));

        Assert.Equal(IpcCallerRole.None, rule.AllowedRoles);
        Assert.False(rule.Allows(IpcCallerRole.Administrator | IpcCallerRole.Agent | IpcCallerRole.User | IpcCallerRole.Stub | IpcCallerRole.PluginHost));
    }

    [Fact]
    public void Agent_methods_are_not_open_to_ordinary_users()
    {
        Assert.True(IpcMethodPolicy.TryGet(IpcMethods.AgentRegister, out var rule));

        Assert.False(rule.Allows(IpcCallerRole.User | IpcCallerRole.Administrator));
        Assert.True(rule.Allows(IpcCallerRole.User | IpcCallerRole.Agent));
    }

    [Fact]
    public void Frame_limit_is_large_only_for_roles_that_may_install_plugins()
    {
        Assert.Equal(256 * 1024, IpcMethodPolicy.MaxFrameBytesFor(IpcCallerRole.User));
        Assert.Equal(256 * 1024, IpcMethodPolicy.MaxFrameBytesFor(IpcCallerRole.PluginHost));
        Assert.Equal(IpcLimits.MaxMessageBytes, IpcMethodPolicy.MaxFrameBytesFor(IpcCallerRole.User | IpcCallerRole.Administrator));
    }

    [Theory]
    [InlineData("plugin.com.example.focus.getStats", "com.example.focus", "getStats")]
    [InlineData("plugin.a-b.c1.m", "a-b.c1", "m")]
    public void Plugin_methods_are_parsed(string wire, string id, string method)
    {
        Assert.True(IpcMethods.TryParsePluginMethod(wire, out var pluginId, out var parsed));
        Assert.Equal(id, pluginId);
        Assert.Equal(method, parsed);
        Assert.True(IpcMethodPolicy.TryGet(wire, out var rule));
        Assert.Same(IpcMethodPolicy.PluginRule, rule);
        Assert.Equal(wire, IpcMethods.ForPlugin(id, method));
    }

    [Theory]
    [InlineData("plugin.")]
    [InlineData("plugin.focus.getStats")]
    [InlineData("plugin.com.example.focus.")]
    [InlineData("plugin.com.example.focus.Get")]
    [InlineData("plugin.com.example.focus.get-stats")]
    [InlineData("plugin.Com.Example.focus.get")]
    [InlineData("plugin.com.example.focus.get stats")]
    [InlineData("session.getStatus.x")]
    [InlineData("")]
    public void Malformed_plugin_methods_are_not_in_the_contract(string wire)
    {
        Assert.False(IpcMethodPolicy.TryGet(wire, out _));
    }

    [Fact]
    public void Session_status_round_trips_a_snapshot()
    {
        var snapshot = new SessionSnapshot(SessionPhase.EmergencyPass, Guid.NewGuid(), TimeSpan.FromMinutes(42), TimeSpan.FromSeconds(90), 2, 3, TimeSpan.FromMinutes(5));

        var json = JsonSerializer.Serialize(SessionStatusDto.From(snapshot), IpcJson.TypeInfo<SessionStatusDto>());
        var back = JsonSerializer.Deserialize(json, IpcJson.TypeInfo<SessionStatusDto>())!.ToSnapshot();

        Assert.Equal(snapshot, back);
        Assert.Contains("\"phase\":\"EmergencyPass\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Dtos_reject_unknown_members()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"code":"x","extra":1}""", IpcJson.TypeInfo<BreakGlassRequest>()));
    }

    [Fact]
    public void Envelope_rejects_unknown_members()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"id":1,"method":"session.getStatus","admin":true}""");

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(bytes, IpcJsonContext.Default.IpcEnvelope));
    }

    [Fact]
    public void Non_nullable_members_reject_null()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"code":null}""", IpcJson.TypeInfo<BreakGlassRequest>()));
    }

    [Fact]
    public void Unknown_enum_names_are_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"slot":"Everywhere"}""", IpcJson.TypeInfo<GetContributionsRequest>()));
    }

    [Fact]
    public void Excessively_deep_json_is_rejected()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 64)) + "1" + new string('}', 64);
        var bytes = Encoding.UTF8.GetBytes($$"""{"id":1,"method":"m","params":{{deep}}}""");

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(bytes, IpcJsonContext.Default.IpcEnvelope));
    }

    [Fact]
    public void Envelope_kinds_are_derived_from_the_members()
    {
        var obj = IpcHarness.Json("{}");

        Assert.Equal(IpcMessageKind.Request, IpcEnvelope.Request(1, "m", null).Kind);
        Assert.Equal(IpcMessageKind.Notification, IpcEnvelope.Notification("m", obj).Kind);
        Assert.Equal(IpcMessageKind.Response, IpcEnvelope.Success(1, obj).Kind);
        Assert.Equal(IpcMessageKind.Response, IpcEnvelope.Failure(1, IpcErrorCode.NotFound, "x").Kind);
        Assert.Equal(IpcMessageKind.Response, IpcEnvelope.Failure(null, IpcErrorCode.PayloadTooLarge, "x").Kind);
        Assert.Equal(IpcMessageKind.Invalid, new IpcEnvelope(null, null, null, null, null).Kind);
        Assert.Equal(IpcMessageKind.Invalid, new IpcEnvelope(1, "m", null, obj, null).Kind);
        Assert.Equal(IpcMessageKind.Invalid, new IpcEnvelope(1, null, null, obj, new IpcError(IpcErrorCode.Internal, "x")).Kind);
        Assert.Equal(IpcMessageKind.Invalid, new IpcEnvelope(null, null, null, obj, null).Kind);
    }

    [Fact]
    public void Error_codes_have_stable_wire_names()
    {
        var json = JsonSerializer.Serialize(IpcEnvelope.Failure(7, IpcErrorCode.NotAllowedDuringSession, "no"), IpcJsonContext.Default.IpcEnvelope);

        Assert.Equal("""{"id":7,"error":{"code":"NotAllowedDuringSession","message":"no"}}""", json);
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8],
            Enum.GetValues<IpcErrorCode>().Select(c => (int)c).Order());
    }

    [Fact]
    public void Ui_actions_round_trip_through_the_contract()
    {
        var request = new SendActionRequest("com.example.focus", "main", UiAction.Submit("form", new Dictionary<string, string> { ["name"] = "x" }));

        var back = IpcJson.FromElement<SendActionRequest>(IpcJson.ToElement(request));

        Assert.Equal("form", back.Action.ElementId);
        Assert.Equal(UiActionKind.Submit, back.Action.Kind);
        Assert.Equal("x", back.Action.FormValues["name"]);
    }

    [Fact]
    public void Ui_trees_travel_as_validated_ui_json()
    {
        var json = UiJson.Serialize(new UiText("hi"));
        var response = new GetContributionsResponse(UiSlot.BlockScreen, [new UiContributionDto("com.example.focus", "c", null, json)]);

        var back = IpcJson.FromElement<GetContributionsResponse>(IpcJson.ToElement(response));

        Assert.Equal("hi", Assert.IsType<UiText>(UiJson.Deserialize(back.Items[0].Json)).Text);
    }
}
