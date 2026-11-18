using Blocker.Core.Ports;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// <see cref="IBrowserFirewall"/> on a WFP dynamic session: filters exist only while the session handle is open, so service
/// death removes them. Enabling again installs the new session before closing the old one (no unprotected gap).
/// Browser executables that do not exist are skipped. The filter set is described by <see cref="WfpFilterPlan"/>.
/// </summary>
public sealed class WfpBrowserFirewall : IBrowserFirewall
{
    private readonly Func<WfpFilterPlan, IDisposable> _install;
    private readonly object _gate = new();
    private IDisposable? _session;

    /// <summary>Creates a firewall that talks to the real Windows Filtering Platform (requires administrator rights).</summary>
    public WfpBrowserFirewall()
        : this(WfpSession.Install)
    {
    }

    internal WfpBrowserFirewall(Func<WfpFilterPlan, IDisposable> install) => _install = install;

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <inheritdoc />
    public Task EnableAsync(IReadOnlyList<string> browserExecutables, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WfpFilterPlan plan = WfpFilterPlan.Create(browserExecutables);
        lock (_gate)
        {
            IDisposable? next = plan.Filters.Count == 0 ? null : _install(plan);
            IDisposable? previous = _session;
            _session = next;
            previous?.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisableAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IDisposable? previous = _session;
            _session = null;
            previous?.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _ = DisableAsync(CancellationToken.None);
        return ValueTask.CompletedTask;
    }
}
