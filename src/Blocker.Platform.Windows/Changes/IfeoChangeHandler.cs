using Blocker.Core.Journal;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Registry view an IFEO key lives in: the native (64-bit) view or the WOW6432Node view read by 32-bit processes.</summary>
internal enum IfeoView
{
    /// <summary>HKLM\SOFTWARE (64-bit view).</summary>
    Native,

    /// <summary>HKLM\SOFTWARE\WOW6432Node (32-bit view).</summary>
    Wow64,
}

/// <summary>What one view's IFEO key looked like before Blocker touched it.</summary>
/// <param name="KeyExisted">The image key existed.</param>
/// <param name="Debugger">Prior <c>Debugger</c> value (null: absent).</param>
internal sealed record IfeoViewState(bool KeyExisted, string? Debugger);

/// <summary>Original state of an IFEO target in both registry views.</summary>
/// <param name="Native">64-bit view.</param>
/// <param name="Wow64">32-bit view.</param>
internal sealed record IfeoOriginalState(IfeoViewState Native, IfeoViewState Wow64);

/// <summary>Desired IFEO state: the debugger stub that replaces the image.</summary>
/// <param name="StubPath">Absolute path of the stub executable; written as <c>"stub" --ifeo</c>.</param>
internal sealed record IfeoDesiredState(string StubPath);

/// <summary>The values Blocker cares about in one IFEO image key.</summary>
/// <param name="Debugger">Current <c>Debugger</c> value.</param>
/// <param name="Owner">Current <c>BlockerOwned</c> marker.</param>
internal sealed record IfeoKeySnapshot(string? Debugger, string? Owner);

/// <summary>Registry access for IFEO keys (faked in tests).</summary>
internal interface IIfeoStore
{
    /// <summary>Reads the image key, or null when it does not exist.</summary>
    IfeoKeySnapshot? Read(IfeoView view, string imageName);

    /// <summary>
    /// Creates the key if needed and sets <c>Debugger</c> and the marker; null removes the value. Only these two values are
    /// touched. Order: the marker is written before the debugger and removed after it, so a marker is present whenever our
    /// debugger might be.
    /// </summary>
    void Write(IfeoView view, string imageName, string? debugger, string? owner);

    /// <summary>Deletes the image key when it has no values and no subkeys; returns whether it was deleted.</summary>
    bool DeleteKeyIfEmpty(IfeoView view, string imageName);

    /// <summary>Names of all image keys in the view.</summary>
    IReadOnlyList<string> ListImages(IfeoView view);
}

/// <summary>
/// Image File Execution Options redirection (<c>Debugger</c> value) for one executable. Both registry views are handled
/// because IFEO is read per process bitness on 64-bit Windows (HKLM\SOFTWARE and HKLM\SOFTWARE\WOW6432Node); where Windows
/// shares the key, the second view is a harmless no-op. Only <c>Debugger</c> and <c>BlockerOwned</c> are touched, so values
/// such as <c>GlobalFlag</c> survive. Restore acts only while our marker is present (idempotent, never clobbers foreign edits).
/// A pre-existing <c>Debugger</c> is restored as REG_SZ.
/// </summary>
public sealed class IfeoChangeHandler : IReversibleChangeHandler
{
    private static readonly IfeoView[] Views = [IfeoView.Native, IfeoView.Wow64];

    private readonly IIfeoStore _store;
    private readonly string _installId;
    private readonly HashSet<string> _criticalNames;

    /// <summary>Creates a handler over HKLM.</summary>
    /// <param name="installId">Value of the <c>BlockerOwned</c> marker.</param>
    /// <param name="criticalImageNames">Image names (e.g. <c>explorer.exe</c>) that must never be redirected.</param>
    public IfeoChangeHandler(string installId, IEnumerable<string> criticalImageNames)
        : this(installId, criticalImageNames, new RegistryIfeoStore())
    {
    }

    internal IfeoChangeHandler(string installId, IEnumerable<string> criticalImageNames, IIfeoStore store)
    {
        _installId = ChangeGuard.RequireInstallId(installId);
        _criticalNames = new HashSet<string>(criticalImageNames, StringComparer.OrdinalIgnoreCase);
        _store = store;
    }

    /// <inheritdoc />
    public string Kind => "ifeo";

    /// <summary>Validates an image file name: simple <c>name.exe</c>, no path characters, not a critical process.</summary>
    /// <exception cref="ArgumentException">The name is not acceptable.</exception>
    internal static string ValidateTarget(string? target, ISet<string> criticalNames)
    {
        if (string.IsNullOrEmpty(target) || target.Length > 255 || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || target.Length == 4 || target[^5] is '.' or ' '
            || target.Any(c => c < ' ' || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            || target.StartsWith(' ') || target.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{target}' is not a simple executable file name.", nameof(target));
        }

        return criticalNames.Contains(target)
            ? throw new ArgumentException($"'{target}' is a critical system process and can never be redirected.", nameof(target))
            : target;
    }

    /// <inheritdoc />
    public ValueTask<string?> CaptureOriginalAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ValidateTarget(target, _criticalNames);

        // A marker already present means the visible Debugger is ours (stale): the real original is unknowable, treat as absent.
        IfeoViewState Capture(IfeoView view) => _store.Read(view, name) switch
        {
            null => new IfeoViewState(false, null),
            { Owner: not null } => new IfeoViewState(true, null),
            { } snapshot => new IfeoViewState(true, snapshot.Debugger),
        };

        string json = ChangeGuard.ToJson(new IfeoOriginalState(Capture(IfeoView.Native), Capture(IfeoView.Wow64)), ChangesJsonContext.Default.IfeoOriginalState);
        return ValueTask.FromResult<string?>(json);
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(string target, string desiredStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ValidateTarget(target, _criticalNames);
        IfeoDesiredState desired = ChangeGuard.FromJson(desiredStateJson, ChangesJsonContext.Default.IfeoDesiredState, nameof(desiredStateJson));
        string stub = WindowsPath.Normalize(desired.StubPath, nameof(desiredStateJson));
        if (!stub.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The IFEO stub must be an .exe.", nameof(desiredStateJson));
        }

        string debugger = $"\"{stub}\" --ifeo";
        foreach (IfeoView view in Views)
        {
            _store.Write(view, name, debugger, _installId);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RestoreAsync(string target, string? originalStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = ValidateTarget(target, _criticalNames);
        IfeoOriginalState? original = originalStateJson is null
            ? null
            : ChangeGuard.FromJson(originalStateJson, ChangesJsonContext.Default.IfeoOriginalState, nameof(originalStateJson));

        foreach (IfeoView view in Views)
        {
            if (_store.Read(view, name) is not { Owner: not null })
            {
                continue; // absent or not ours (already restored): leave untouched
            }

            IfeoViewState? prior = view == IfeoView.Native ? original?.Native : original?.Wow64;
            _store.Write(view, name, prior?.Debugger, owner: null);
            if (prior is not { KeyExisted: true })
            {
                _store.DeleteKeyIfEmpty(view, name);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> SweepAsync(string installId, CancellationToken cancellationToken)
    {
        string id = ChangeGuard.RequireInstallId(installId);
        int removed = 0;
        foreach (IfeoView view in Views)
        {
            foreach (string name in _store.ListImages(view))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_store.Read(view, name) is { Owner: { } owner } && string.Equals(owner, id, StringComparison.Ordinal))
                {
                    _store.Write(view, name, debugger: null, owner: null);
                    _store.DeleteKeyIfEmpty(view, name);
                    removed++;
                }
            }
        }

        return ValueTask.FromResult(removed);
    }
}
