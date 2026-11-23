namespace Blocker.Core.Orchestration;

/// <summary>One journaled system change the platform wants for the session.</summary>
/// <param name="Kind">Journal handler kind (<c>ifeo</c>, <c>regpolicy</c>, <c>servicedacl</c>, <c>ntfsdeny</c>).</param>
/// <param name="Target">Handler-specific target.</param>
/// <param name="DesiredStateJson">Desired state in the handler's JSON format.</param>
/// <param name="BestEffort">
/// True for hardening that must not decide whether a session can start (it may be refused by security software or a protected key):
/// a failure to apply it is logged and reported as a warning instead of rolling the session back.
/// </param>
public sealed record PlannedChange(string Kind, string Target, string DesiredStateJson, bool BestEffort = false);

/// <summary>Everything the platform needs to plan the changes of one session.</summary>
/// <param name="ProxyPort">Port of the running filter proxy (for the browser PAC policies).</param>
/// <param name="IfeoImageNames">Image names (<c>name.exe</c>) to redirect to the stub.</param>
/// <param name="StubExecutablePath">The stub the IFEO entries point at.</param>
/// <param name="ProtectedDirectories">Directories that get deny ACEs.</param>
/// <param name="ServiceNames">Services whose DACL is hardened (Blocker service and watchdog).</param>
public sealed record SessionPlanInput(
    int ProxyPort,
    IReadOnlyList<string> IfeoImageNames,
    string StubExecutablePath,
    IReadOnlyList<string> ProtectedDirectories,
    IReadOnlyList<string> ServiceNames);

/// <summary>Turns a <see cref="SessionPlanInput"/> into the ordered changes to journal; implemented by the platform layer (Core never references it).</summary>
public interface IEnforcementChangePlanner
{
    /// <summary>Returns the changes in the order they must be applied (rollback runs in reverse).</summary>
    IReadOnlyList<PlannedChange> PlanSessionChanges(SessionPlanInput input);
}
