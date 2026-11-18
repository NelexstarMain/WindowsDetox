using Blocker.Core.Orchestration;
using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Planning;

/// <summary>
/// Turns the inputs of a session into the journaled Windows changes, in the order they are applied (rollback runs in reverse):
/// <list type="number">
/// <item><c>ifeo</c>: one Image File Execution Options redirect per image name, pointing at the block screen (<c>"stub" --ifeo</c>).</item>
/// <item><c>regpolicy</c>: the browser policies from <see cref="BrowserPolicyPlan"/> that route browsers through the local PAC proxy.</item>
/// <item><c>ntfsdeny</c>: deny ACEs on every protected directory.</item>
/// <item><c>servicedacl</c>: the restricted DACL of both services.</item>
/// </list>
/// Image names the IFEO handler would refuse (not a simple <c>name.exe</c>, or critical) are left out rather than failing the whole session start.
/// </summary>
public sealed class WindowsChangePlanner : IEnforcementChangePlanner
{
    private readonly HashSet<string> _criticalImageNames;

    /// <summary>Creates a planner.</summary>
    /// <param name="criticalImageNames">Image names (for example <c>explorer.exe</c>) that must never be redirected; the same set the IFEO handler enforces.</param>
    public WindowsChangePlanner(IEnumerable<string>? criticalImageNames = null) =>
        _criticalImageNames = new HashSet<string>(criticalImageNames ?? [], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A directory or the stub is not a normalized drive-absolute path, or a service name is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The proxy port is not in 1..65535.</exception>
    public IReadOnlyList<PlannedChange> PlanSessionChanges(SessionPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string stub = WindowsPath.Normalize(input.StubExecutablePath, nameof(input));
        List<PlannedChange> plan = [];

        string ifeoState = ChangeGuard.ToJson(new IfeoDesiredState(stub), ChangesJsonContext.Default.IfeoDesiredState);
        foreach (string name in input.IfeoImageNames.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsRedirectable(name))
            {
                plan.Add(new PlannedChange("ifeo", name, ifeoState));
            }
        }

        plan.AddRange(BrowserPolicyPlan.Create(input.ProxyPort).Select(static e => new PlannedChange("regpolicy", e.Target, e.DesiredStateJson)));

        string denyState = ChangeGuard.ToJson(new NtfsDenyDesiredState(), ChangesJsonContext.Default.NtfsDenyDesiredState);
        foreach (string directory in input.ProtectedDirectories.Select(d => WindowsPath.Normalize(d, nameof(input))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            plan.Add(new PlannedChange("ntfsdeny", directory, denyState));
        }

        string daclState = ChangeGuard.ToJson(new ServiceDaclDesiredState(), ChangesJsonContext.Default.ServiceDaclDesiredState);
        foreach (string service in input.ServiceNames.Select(n => ChangeGuard.RequireServiceName(n, nameof(input))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            plan.Add(new PlannedChange("servicedacl", service, daclState));
        }

        return plan;
    }

    private bool IsRedirectable(string name)
    {
        try
        {
            _ = IfeoChangeHandler.ValidateTarget(name, _criticalImageNames);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
