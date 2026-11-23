using Blocker.Core.Journal;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Original service DACL.</summary>
/// <param name="Sddl">DACL in SDDL form.</param>
internal sealed record ServiceDaclOriginalState(string Sddl);

/// <summary>Desired service DACL.</summary>
/// <param name="Sddl">Custom DACL (must pass <see cref="ServiceSddl.IsSafe"/>); null selects <see cref="ServiceSddl.BuildRestricted"/>.</param>
internal sealed record ServiceDaclDesiredState(string? Sddl = null);

/// <summary>Service security access (faked in tests).</summary>
internal interface IServiceSecurity
{
    /// <summary>The DACL of the service in SDDL form, or null when the service does not exist.</summary>
    string? QuerySddl(string serviceName);

    /// <summary>Replaces the DACL; false when the service does not exist.</summary>
    bool TrySetSddl(string serviceName, string sddl);
}

/// <summary>
/// Restricts who may stop, reconfigure or delete a service by replacing its DACL (SYSTEM keeps full control; Administrators,
/// Interactive and Authenticated Users get query/interrogate/start only). A DACL cannot carry an ownership marker, so
/// <see cref="SweepAsync"/> relies on the optional per-service default SDDL supplied at construction (captured at install
/// time): a service whose current DACL equals the restricted SDDL is reset to its default. Without defaults, Sweep is a no-op.
/// The same dictionary names the only services <see cref="RestoreAsync"/> will touch.
/// </summary>
public sealed class ServiceDaclChangeHandler : IReversibleChangeHandler
{
    private readonly IServiceSecurity _security;
    private readonly IReadOnlyDictionary<string, string> _defaultSddl;

    /// <summary>Creates a handler over the SCM.</summary>
    /// <param name="defaultSddlByService">Default SDDL per service name (captured at install): the services this installation owns, used by Sweep and the restore check.</param>
    public ServiceDaclChangeHandler(IReadOnlyDictionary<string, string>? defaultSddlByService = null)
        : this(new ScmServiceSecurity(), defaultSddlByService)
    {
    }

    internal ServiceDaclChangeHandler(IServiceSecurity security, IReadOnlyDictionary<string, string>? defaultSddlByService)
    {
        _security = security;
        _defaultSddl = defaultSddlByService ?? new Dictionary<string, string>();
    }

    /// <inheritdoc />
    public string Kind => "servicedacl";

    /// <inheritdoc />
    public ValueTask<string?> CaptureOriginalAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ChangeGuard.RequireServiceName(target);
        string? sddl = _security.QuerySddl(name);
        return ValueTask.FromResult(sddl is null ? null : ChangeGuard.ToJson(new ServiceDaclOriginalState(sddl), ChangesJsonContext.Default.ServiceDaclOriginalState));
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(string target, string desiredStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ChangeGuard.RequireServiceName(target);
        ServiceDaclDesiredState desired = ChangeGuard.FromJson(desiredStateJson, ChangesJsonContext.Default.ServiceDaclDesiredState, nameof(desiredStateJson));
        string sddl = desired.Sddl ?? ServiceSddl.BuildRestricted();
        if (!ServiceSddl.IsSafe(sddl))
        {
            throw new ArgumentException("DACL must keep SYSTEM in full control and grant others at most query/interrogate/start.", nameof(desiredStateJson));
        }

        return _security.TrySetSddl(name, sddl)
            ? ValueTask.CompletedTask
            : throw new InvalidOperationException($"Service '{name}' does not exist.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The journal is replayed as SYSTEM and its file can be forged by anyone who can write the data root, so only the services this
    /// installation owns are restored, and only to a DACL that <see cref="ServiceSddl.IsSafeToRestore"/> accepts.
    /// </remarks>
    public ValueTask RestoreAsync(string target, string? originalStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ChangeGuard.RequireServiceName(target);
        if (!_defaultSddl.TryGetValue(name, out string? recordedDefault))
        {
            throw new ArgumentException($"'{name}' is not a service of this installation.", nameof(target));
        }

        if (originalStateJson is not null)
        {
            // Idempotent: setting the same DACL again is harmless; a service that no longer exists needs no restore.
            ServiceDaclOriginalState original = ChangeGuard.FromJson(originalStateJson, ChangesJsonContext.Default.ServiceDaclOriginalState, nameof(originalStateJson));
            if (!ServiceSddl.IsSafeToRestore(original.Sddl, recordedDefault))
            {
                throw new ArgumentException($"The recorded DACL of '{name}' lets an unprivileged account reconfigure or stop it; refusing to restore it.", nameof(originalStateJson));
            }

            _security.TrySetSddl(name, original.Sddl);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> SweepAsync(string installId, CancellationToken cancellationToken)
    {
        string restricted = ServiceSddl.BuildRestricted();
        int removed = 0;
        foreach ((string service, string defaultSddl) in _defaultSddl)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = ChangeGuard.RequireServiceName(service, nameof(service));
            if (string.Equals(_security.QuerySddl(name), restricted, StringComparison.OrdinalIgnoreCase)
                && _security.TrySetSddl(name, defaultSddl))
            {
                removed++;
            }
        }

        return ValueTask.FromResult(removed);
    }
}
