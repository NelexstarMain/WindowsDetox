using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;

namespace Blocker.Core.Tests.Ipc;

/// <summary>The single handler behind every <c>plugin.{id}.{method}</c> request, and the contract of the install response.</summary>
public sealed class PluginMethodHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_handler_serves_every_plugin_method_and_sees_the_wire_name()
    {
        await using IpcHarness h = new IpcHarness(IpcCallerRole.User).Started();
        h.Server.RegisterPluginHandler((context, parameters, _) =>
            ValueTask.FromResult(IpcHarness.Json($$"""{"method":"{{context.Method}}","n":{{parameters.GetProperty("n").GetInt32()}}}""")));

        JsonElement reply = await h.NewClient().InvokeAsync(IpcMethods.ForPlugin("com.example.focus", "ping"), IpcHarness.Json("""{"n":7}"""), Ct);

        Assert.Equal("plugin.com.example.focus.ping", reply.GetProperty("method").GetString());
        Assert.Equal(7, reply.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Without_a_plugin_handler_plugin_methods_are_unknown_and_the_handler_can_be_removed()
    {
        await using IpcHarness h = new IpcHarness(IpcCallerRole.User).Started();
        IpcClient client = h.NewClient();
        IDisposable registration = h.Server.RegisterPluginHandler((_, _, _) => ValueTask.FromResult(IpcHarness.Json("{}")));
        _ = await client.InvokeAsync(IpcMethods.ForPlugin("com.example.focus", "ping"), IpcHarness.Json("{}"), Ct);

        registration.Dispose();
        IpcException error = await Assert.ThrowsAsync<IpcException>(async () => await client.InvokeAsync(IpcMethods.ForPlugin("com.example.focus", "ping"), IpcHarness.Json("{}"), Ct));

        Assert.Equal(IpcErrorCode.NotFound, error.Code);
    }

    [Fact]
    public async Task Only_one_plugin_handler_can_be_registered()
    {
        await using IpcHarness h = new IpcHarness(IpcCallerRole.User).Started();
        _ = h.Server.RegisterPluginHandler((_, _, _) => ValueTask.FromResult(IpcHarness.Json("{}")));

        _ = Assert.Throws<InvalidOperationException>(() => h.Server.RegisterPluginHandler((_, _, _) => ValueTask.FromResult(IpcHarness.Json("{}"))));
    }

    [Fact]
    public async Task The_plugin_handler_does_not_bypass_the_role_check_of_plugin_methods()
    {
        await using IpcHarness h = new IpcHarness(IpcCallerRole.Agent).Started();
        h.Server.RegisterPluginHandler((_, _, _) => ValueTask.FromResult(IpcHarness.Json("{}")));

        IpcException error = await Assert.ThrowsAsync<IpcException>(async () =>
            await h.NewClient().InvokeAsync(IpcMethods.ForPlugin("com.example.focus", "ping"), IpcHarness.Json("{}"), Ct));

        Assert.Equal(IpcErrorCode.Unauthorized, error.Code);
    }

    [Fact]
    public void An_install_response_without_the_new_members_still_parses_and_the_new_members_round_trip()
    {
        InstallPluginResponse old = IpcJson.FromElement<InstallPluginResponse>(IpcHarness.Json("""{"success":false,"errors":["x"]}"""));
        var full = new InstallPluginResponse(false, "com.a.b", ["Failed: T: no"], "Tests", [new PluginTestResultDto("T", PluginTestOutcomeDto.TimedOut, "slow"), new PluginTestResultDto("U", PluginTestOutcomeDto.Passed)]);

        InstallPluginResponse back = IpcJson.FromElement<InstallPluginResponse>(IpcJson.ToElement(full));

        Assert.Null(old.FailedStage);
        Assert.Null(old.Tests);
        Assert.Equal(full.FailedStage, back.FailedStage);
        Assert.Equal(full.Tests, back.Tests);
        Assert.Equal(full.Errors, back.Errors);
        Assert.DoesNotContain("failedStage", IpcJson.ToElement(new InstallPluginResponse(true, "a.b", [])).GetRawText(), StringComparison.Ordinal);
    }
}
