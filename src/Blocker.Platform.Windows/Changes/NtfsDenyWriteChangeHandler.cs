using System.Security.AccessControl;
using Blocker.Core.Journal;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Original directory DACL.</summary>
/// <param name="Sddl">DACL in SDDL form (with any stale Blocker deny ACEs removed).</param>
internal sealed record NtfsDenyOriginalState(string Sddl);

/// <summary>Desired state marker; the deny ACEs themselves are fixed by <see cref="NtfsDenyAcl"/>.</summary>
internal sealed record NtfsDenyDesiredState;

/// <summary>A directory as seen by the handler.</summary>
/// <param name="IsReparsePoint">The directory is a junction or symbolic link.</param>
/// <param name="Sddl">DACL in SDDL form.</param>
internal sealed record DirectoryAclSnapshot(bool IsReparsePoint, string Sddl);

/// <summary>Directory ACL access (faked in tests).</summary>
internal interface IDirectoryAclStore
{
    /// <summary>Reads the directory; null when it does not exist.</summary>
    DirectoryAclSnapshot? Read(string path);

    /// <summary>Replaces the directory DACL from an SDDL string.</summary>
    void WriteSddl(string path, string sddl);
}

/// <summary>
/// Protects a configured directory (install dir, ProgramData\WindowsDetox) with explicit DENY ACEs for BUILTIN\Administrators
/// and BUILTIN\Users (Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership; inherited by children)
/// while SYSTEM keeps full control. Only the configured directories themselves are valid targets, in normalized form, and
/// reparse points are refused. ACEs cannot carry an ownership marker, so Restore only acts while our deny ACEs are present
/// and Sweep removes every deny ACE that matches our SIDs, rights mask and inheritance exactly on the configured roots
/// (the install id is therefore not used; the count is the number of ACEs removed).
/// </summary>
public sealed class NtfsDenyWriteChangeHandler : IReversibleChangeHandler
{
    private readonly IDirectoryAclStore _store;
    private readonly IDenyAclTransform _acl;
    private readonly string[] _roots;

    /// <summary>Creates a handler over the file system.</summary>
    /// <param name="allowedRoots">Exactly the directories that may be protected (normalized drive-absolute paths).</param>
    public NtfsDenyWriteChangeHandler(IEnumerable<string> allowedRoots)
        : this(allowedRoots, new FileSystemDirectoryAcl(), new NtfsDenyAcl())
    {
    }

    internal NtfsDenyWriteChangeHandler(IEnumerable<string> allowedRoots, IDirectoryAclStore store, IDenyAclTransform acl)
    {
        _acl = acl;
        _roots = [.. allowedRoots.Select(r => WindowsPath.Normalize(r, nameof(allowedRoots)))];
        if (_roots.Length == 0)
        {
            throw new ArgumentException("At least one root is required.", nameof(allowedRoots));
        }

        _store = store;
    }

    /// <inheritdoc />
    public string Kind => "ntfsdeny";

    /// <summary>Returns the normalized target when it is exactly one of the configured roots.</summary>
    /// <exception cref="ArgumentException">Not normalized (traversal, UNC, forward slashes...) or not a configured root.</exception>
    internal static string ValidateTarget(string? target, IReadOnlyList<string> roots)
    {
        // WindowsPath is purely lexical and strict: it already rejects '.', '..', UNC/device paths and forward slashes.
        string normalized = WindowsPath.Normalize(target, nameof(target));
        return roots.Any(r => WindowsPath.AreEqual(r, normalized))
            ? normalized
            : throw new ArgumentException($"'{target}' is not one of the protected directories.", nameof(target));
    }

    /// <inheritdoc />
    public ValueTask<string?> CaptureOriginalAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ValidateTarget(target, _roots);
        if (_store.Read(path) is not { } snapshot)
        {
            return ValueTask.FromResult<string?>(null);
        }

        RefuseReparsePoint(path, snapshot);
        string sddl = _acl.HasDenyAces(snapshot.Sddl) ? _acl.RemoveDenyAces(snapshot.Sddl).Sddl : snapshot.Sddl; // stale denies are never "original"
        return ValueTask.FromResult<string?>(ChangeGuard.ToJson(new NtfsDenyOriginalState(sddl), ChangesJsonContext.Default.NtfsDenyOriginalState));
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(string target, string desiredStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ValidateTarget(target, _roots);
        _ = ChangeGuard.FromJson(desiredStateJson, ChangesJsonContext.Default.NtfsDenyDesiredState, nameof(desiredStateJson));
        DirectoryAclSnapshot snapshot = _store.Read(path) ?? throw new DirectoryNotFoundException($"Directory '{path}' does not exist.");
        RefuseReparsePoint(path, snapshot);

        string updated = _acl.AddDenyAces(snapshot.Sddl);
        if (!string.Equals(updated, snapshot.Sddl, StringComparison.Ordinal))
        {
            _store.WriteSddl(path, updated);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RestoreAsync(string target, string? originalStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ValidateTarget(target, _roots);
        if (originalStateJson is null || _store.Read(path) is not { } snapshot)
        {
            return ValueTask.CompletedTask; // never changed, or the directory is gone
        }

        RefuseReparsePoint(path, snapshot);
        NtfsDenyOriginalState original = ChangeGuard.FromJson(originalStateJson, ChangesJsonContext.Default.NtfsDenyOriginalState, nameof(originalStateJson));
        if (_acl.HasDenyAces(snapshot.Sddl))
        {
            // The journal can be forged: a recorded DACL that would newly let an unprivileged account modify the install or data directory
            // (and so plant code that runs as SYSTEM) is never written back. Dropping only our own deny ACEs is the rollback that remains.
            string withoutDenies = _acl.RemoveDenyAces(snapshot.Sddl).Sddl;
            string restored = DaclInspector.GrantsNothingDangerousToUnprivileged(original.Sddl, DaclInspector.FileModify, withoutDenies)
                ? original.Sddl
                : withoutDenies;
            _store.WriteSddl(path, restored);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> SweepAsync(string installId, CancellationToken cancellationToken)
    {
        int removed = 0;
        foreach (string root in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_store.Read(root) is not { IsReparsePoint: false } snapshot)
            {
                continue;
            }

            (string sddl, int count) = _acl.RemoveDenyAces(snapshot.Sddl);
            if (count > 0)
            {
                _store.WriteSddl(root, sddl);
                removed += count;
            }
        }

        return ValueTask.FromResult(removed);
    }

    private static void RefuseReparsePoint(string path, DirectoryAclSnapshot snapshot)
    {
        if (snapshot.IsReparsePoint)
        {
            throw new InvalidOperationException($"'{path}' is a reparse point (junction or symbolic link); refusing to change its ACL.");
        }
    }
}

/// <summary>File-system backed <see cref="IDirectoryAclStore"/> (FileSystemAclExtensions).</summary>
internal sealed class FileSystemDirectoryAcl : IDirectoryAclStore
{
    public DirectoryAclSnapshot? Read(string path)
    {
        DirectoryInfo directory = new(path);
        if (!directory.Exists)
        {
            return null;
        }

        string sddl = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        return new DirectoryAclSnapshot((directory.Attributes & FileAttributes.ReparsePoint) != 0, sddl);
    }

    public void WriteSddl(string path, string sddl)
    {
        DirectorySecurity security = new();
        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
