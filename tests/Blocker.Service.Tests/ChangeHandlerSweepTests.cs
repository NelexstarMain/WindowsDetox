using Blocker.Core.Journal;
using Blocker.Core.ServiceHost;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Service.Tests;

public sealed class ChangeHandlerSweepTests
{
    private static readonly BlockerPaths Paths = BlockerPaths.Create(@"C:\ProgramData", @"C:\Program Files\WindowsDetox");
    private static readonly string Restricted = ServiceSddl.BuildRestricted();

    private readonly InMemoryServiceSecurity _security = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sweep_resets_both_restricted_services_to_the_scm_default_dacl()
    {
        _security.Sddl[BlockerServiceNames.Service] = Restricted;
        _security.Sddl[BlockerServiceNames.Watchdog] = Restricted;
        IReversibleChangeHandler handler = WindowsServicePorts.CreateChangeHandlers("install-1", Paths, _security).Single(static h => h.Kind == "servicedacl");

        int removed = await handler.SweepAsync("install-1", _ct);

        Assert.Equal(2, removed);
        Assert.Equal(ServiceSddl.ScmDefaultDacl, _security.Sddl[BlockerServiceNames.Service]);
        Assert.Equal(ServiceSddl.ScmDefaultDacl, _security.Sddl[BlockerServiceNames.Watchdog]);
    }

    private sealed class InMemoryServiceSecurity : IServiceSecurity
    {
        public Dictionary<string, string> Sddl { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? QuerySddl(string serviceName) => Sddl.GetValueOrDefault(serviceName);

        public bool TrySetSddl(string serviceName, string sddl)
        {
            if (!Sddl.ContainsKey(serviceName))
            {
                return false;
            }

            Sddl[serviceName] = sddl;
            return true;
        }
    }
}
