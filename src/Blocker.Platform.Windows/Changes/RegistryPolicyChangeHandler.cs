using Blocker.Core.Journal;
using Microsoft.Win32;

namespace Blocker.Platform.Windows.Changes;

/// <summary>A registry value (any kind, so prior state can be restored exactly).</summary>
/// <param name="Kind">Registry value kind.</param>
/// <param name="Text">String / ExpandString data.</param>
/// <param name="Lines">MultiString data.</param>
/// <param name="Number">DWord (0..uint.MaxValue) or QWord data.</param>
/// <param name="Bytes">Binary / None data.</param>
internal sealed record PolicyValue(RegistryValueKind Kind, string? Text = null, string[]? Lines = null, long? Number = null, byte[]? Bytes = null)
{
    /// <summary>A REG_SZ value.</summary>
    public static PolicyValue String(string text) => new(RegistryValueKind.String, Text: text);

    /// <summary>A REG_DWORD value.</summary>
    public static PolicyValue DWord(uint number) => new(RegistryValueKind.DWord, Number: number);
}

/// <summary>Original state of a policy value.</summary>
/// <param name="ExistingDepth">How many leading segments of the key path existed (== segment count: the key existed).</param>
/// <param name="Value">The prior value, or null when absent.</param>
internal sealed record RegistryPolicyOriginalState(int ExistingDepth, PolicyValue? Value);

/// <summary>An ownership marker found during a sweep.</summary>
/// <param name="KeyPath">Key holding the marker.</param>
/// <param name="ValueName">Name of the owned value (marker name without prefix).</param>
/// <param name="Owner">Install id stored in the marker.</param>
internal sealed record PolicyMarker(string KeyPath, string ValueName, string Owner);

/// <summary>Registry access for policy values (faked in tests).</summary>
internal interface IRegistryPolicyStore
{
    /// <summary>Number of leading segments of <paramref name="keyPath"/> that exist.</summary>
    int CountExistingSegments(string keyPath);

    /// <summary>Reads a value; null when the key or value is absent.</summary>
    PolicyValue? ReadValue(string keyPath, string name);

    /// <summary>Writes a value, creating the key chain.</summary>
    void WriteValue(string keyPath, string name, PolicyValue value);

    /// <summary>Deletes a value if present.</summary>
    void DeleteValue(string keyPath, string name);

    /// <summary>Deletes the key if it has neither values nor subkeys; returns whether it was deleted.</summary>
    bool DeleteKeyIfEmpty(string keyPath);

    /// <summary>All <c>BlockerOwned.*</c> markers in <paramref name="rootKeyPath"/> and its subkeys.</summary>
    IReadOnlyList<PolicyMarker> FindMarkers(string rootKeyPath);
}

/// <summary>
/// Browser policy values below an allowlist of HKLM policy roots (Chrome, Edge, Brave, Firefox and their subkeys). Target is
/// <c>"&lt;keyPath&gt;|&lt;valueName&gt;"</c>; the marker is the sibling string value <c>BlockerOwned.&lt;valueName&gt;</c> = install id.
/// Capture records the value and how many key segments already existed so Restore removes exactly the keys Blocker created.
/// Capture always returns a state record (even for "absent") because it carries the existing key depth.
/// </summary>
public sealed class RegistryPolicyChangeHandler : IReversibleChangeHandler
{
    /// <summary>Prefix of the sibling marker value.</summary>
    internal const string MarkerPrefix = PlatformMarker.MarkerValueName + ".";

    /// <summary>Policy roots browsers read (HKLM).</summary>
    internal static readonly string[] DefaultRoots =
    [
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
        @"SOFTWARE\Policies\Mozilla\Firefox",
    ];

    private readonly IRegistryPolicyStore _store;
    private readonly string _installId;
    private readonly string[] _roots;

    /// <summary>Creates a handler over HKLM and the browser policy roots.</summary>
    /// <param name="installId">Value of the ownership marker.</param>
    public RegistryPolicyChangeHandler(string installId)
        : this(installId, RegistryHive.LocalMachine, DefaultRoots)
    {
    }

    /// <summary>Creates a handler over another hive/roots; intended for tests (e.g. HKCU\Software\WindowsDetoxTests\...).</summary>
    /// <param name="installId">Value of the ownership marker.</param>
    /// <param name="hive">Registry hive.</param>
    /// <param name="allowedRoots">Allowed key paths (and their subkeys) below the hive.</param>
    public RegistryPolicyChangeHandler(string installId, RegistryHive hive, IEnumerable<string> allowedRoots)
        : this(installId, new RegistryPolicyStore(hive), allowedRoots)
    {
    }

    internal RegistryPolicyChangeHandler(string installId, IRegistryPolicyStore store, IEnumerable<string> allowedRoots)
    {
        _installId = ChangeGuard.RequireInstallId(installId);
        _store = store;
        _roots = [.. allowedRoots.Select(r => r.Trim('\\'))];
    }

    /// <inheritdoc />
    public string Kind => "regpolicy";

    /// <summary>Builds a target string.</summary>
    internal static string FormatTarget(string keyPath, string valueName) => $"{keyPath}|{valueName}";

    /// <inheritdoc />
    public ValueTask<string?> CaptureOriginalAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string keyPath, string name) = ParseTarget(target);
        int depth = _store.CountExistingSegments(keyPath);
        bool stale = _store.ReadValue(keyPath, MarkerPrefix + name) is not null;
        PolicyValue? value = depth == Segments(keyPath).Length && !stale ? _store.ReadValue(keyPath, name) : null;
        string json = ChangeGuard.ToJson(new RegistryPolicyOriginalState(depth, value), ChangesJsonContext.Default.RegistryPolicyOriginalState);
        return ValueTask.FromResult<string?>(json);
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(string target, string desiredStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string keyPath, string name) = ParseTarget(target);
        PolicyValue desired = ChangeGuard.FromJson(desiredStateJson, ChangesJsonContext.Default.PolicyValue, nameof(desiredStateJson));
        bool valid = desired switch
        {
            { Kind: RegistryValueKind.String, Text: not null } => true,
            { Kind: RegistryValueKind.DWord, Number: >= 0 and <= uint.MaxValue } => true,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Desired policy value must be a String with text or a DWord within 0..4294967295.", nameof(desiredStateJson));
        }

        _store.WriteValue(keyPath, MarkerPrefix + name, PolicyValue.String(_installId));
        _store.WriteValue(keyPath, name, desired);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RestoreAsync(string target, string? originalStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string keyPath, string name) = ParseTarget(target);
        RegistryPolicyOriginalState? original = originalStateJson is null
            ? null
            : ChangeGuard.FromJson(originalStateJson, ChangesJsonContext.Default.RegistryPolicyOriginalState, nameof(originalStateJson));

        // Only touch the value while our marker is present; afterwards (already restored) this is a no-op.
        if (_store.ReadValue(keyPath, MarkerPrefix + name) is not null)
        {
            if (original?.Value is { } prior)
            {
                _store.WriteValue(keyPath, name, prior);
            }
            else
            {
                _store.DeleteValue(keyPath, name);
            }

            _store.DeleteValue(keyPath, MarkerPrefix + name);
        }

        if (original is not null)
        {
            PruneEmptyKeys(keyPath, original.ExistingDepth);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> SweepAsync(string installId, CancellationToken cancellationToken)
    {
        string id = ChangeGuard.RequireInstallId(installId);
        int removed = 0;
        foreach (string root in _roots)
        {
            foreach (PolicyMarker marker in _store.FindMarkers(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(marker.Owner, id, StringComparison.Ordinal))
                {
                    continue;
                }

                _store.DeleteValue(marker.KeyPath, marker.ValueName);
                _store.DeleteValue(marker.KeyPath, MarkerPrefix + marker.ValueName);
                PruneEmptyKeys(marker.KeyPath, Segments(root).Length); // never removes the allowlist root itself
                removed++;
            }
        }

        return ValueTask.FromResult(removed);
    }

    private static string[] Segments(string keyPath) => keyPath.Split('\\');

    private void PruneEmptyKeys(string keyPath, int keepDepth)
    {
        string[] segments = Segments(keyPath);
        for (int length = segments.Length; length > keepDepth; length--)
        {
            if (!_store.DeleteKeyIfEmpty(string.Join('\\', segments[..length])))
            {
                break;
            }
        }
    }

    private (string KeyPath, string ValueName) ParseTarget(string target)
    {
        int bar = target?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        if (bar <= 0 || bar == target!.Length - 1)
        {
            throw new ArgumentException("Target must be '<keyPath>|<valueName>'.", nameof(target));
        }

        string keyPath = target[..bar].Trim('\\');
        string name = target[(bar + 1)..];
        string[] segments = Segments(keyPath);
        bool keyOk = segments.All(s => s.Length is > 0 and <= 255 && s is not ("." or "..") && !s.Any(char.IsControl))
            && _roots.Any(r => keyPath.Equals(r, StringComparison.OrdinalIgnoreCase)
                || keyPath.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        if (!keyOk)
        {
            throw new ArgumentException($"Key path '{keyPath}' is outside the allowed browser policy roots.", nameof(target));
        }

        if (name.Length > 255 || name.Any(c => char.IsControl(c) || c == '\\')
            || name.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase)
            || name.Equals(PlatformMarker.MarkerValueName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid policy value name.", nameof(target));
        }

        return (keyPath, name);
    }
}
