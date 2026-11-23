using Blocker.Core.Ports;
using Blocker.Core.ServiceHost;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Service.Tests;

public sealed class ServiceCommandsTests
{
    private static readonly BlockerPaths Paths = BlockerPaths.Create(@"C:\ProgramData", @"C:\Program Files\WindowsDetox");

    private readonly List<string> _log = [];
    private readonly Dictionary<string, string> _serviceArguments = [];

    [Fact]
    public async Task Install_protects_the_data_root_and_provisions_plugin_data_before_the_services_start()
    {
        int code = await RunAsync(ServiceCommandKind.Install);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(
            [
                $"protect {Paths.DataRoot}",
                $"provision {Paths.PluginDataDirectory}",
                $"install {BlockerServiceNames.Service}",
                $"install {BlockerServiceNames.Watchdog}",
                $"start {BlockerServiceNames.Service}",
                $"start {BlockerServiceNames.Watchdog}",
            ],
            _log);
    }

    [Fact]
    public async Task Uninstall_removes_services_and_then_the_plugin_data()
    {
        int code = await RunAsync(ServiceCommandKind.Uninstall);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(
            [
                "rollback",
                $"uninstall {BlockerServiceNames.Watchdog}",
                $"uninstall {BlockerServiceNames.Service}",
                $"remove {Paths.PluginDataDirectory}",
            ],
            _log);
    }

    [Fact]
    public async Task Uninstall_keeps_everything_while_a_session_is_active()
    {
        int code = await RunAsync(ServiceCommandKind.Uninstall, rollbackCode: ExitCodes.SessionActive);

        Assert.Equal(ExitCodes.SessionActive, code);
        Assert.Equal(["rollback"], _log);
    }

    [Fact]
    public async Task Recover_leaves_plugin_data_alone()
    {
        int code = await RunAsync(ServiceCommandKind.Recover);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["rollback"], _log);
    }

    [Fact]
    public async Task Install_fails_cleanly_when_the_directory_cannot_be_secured()
    {
        int code = await RunAsync(ServiceCommandKind.Install, provisionFails: true);

        Assert.Equal(ExitCodes.Failed, code);
        Assert.DoesNotContain(_log, entry => entry.StartsWith("install", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_runs_without_administrator_rights()
    {
        int code = await RunAsync(ServiceCommandKind.Install, administrator: false);

        Assert.Equal(ExitCodes.NotAdministrator, code);
        Assert.Empty(_log);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "--dev-mode")]
    public async Task Install_registers_the_service_with_dev_mode_only_when_asked(bool devMode, string expectedArguments)
    {
        int code = await RunAsync(ServiceCommandKind.Install, devMode: devMode);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(expectedArguments, _serviceArguments[BlockerServiceNames.Service]);
    }

    [Fact]
    public async Task Dev_abort_asks_the_service_and_returns_its_exit_code()
    {
        int code = await RunAsync(ServiceCommandKind.DevAbort);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(["devabort"], _log);
    }

    private Task<int> RunAsync(ServiceCommandKind kind, int rollbackCode = ExitCodes.Success, bool provisionFails = false, bool administrator = true, bool devMode = false)
    {
        var ports = new ServiceCommandPorts(
            new FakeInstaller(_log, _serviceArguments),
            new FakePluginData(_log, provisionFails),
            _ =>
            {
                _log.Add("rollback");
                return Task.FromResult(rollbackCode);
            },
            _ =>
            {
                _log.Add("devabort");
                return Task.FromResult(ExitCodes.Success);
            },
            () => administrator,
            paths => _log.Add($"protect {paths.DataRoot}"));
        return ServiceCommands.RunAsync(kind, Paths, devMode, ports, CancellationToken.None);
    }

    private sealed class FakeInstaller(List<string> log, Dictionary<string, string> arguments) : IServiceInstaller
    {
        public void InstallOrUpdate(string serviceName, string displayName, string executablePath, string serviceArguments)
        {
            log.Add($"install {serviceName}");
            arguments[serviceName] = serviceArguments;
        }

        public void Uninstall(string serviceName) => log.Add($"uninstall {serviceName}");

        public void EnsureStarted(string serviceName) => log.Add($"start {serviceName}");
    }

    private sealed class FakePluginData(List<string> log, bool provisionFails) : IPluginDataDirectory
    {
        public void Provision(string path)
        {
            if (provisionFails)
            {
                throw new UnauthorizedAccessException("denied");
            }

            log.Add($"provision {path}");
        }

        public void Remove(string path) => log.Add($"remove {path}");
    }
}
