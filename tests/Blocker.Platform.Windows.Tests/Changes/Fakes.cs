using Blocker.Platform.Windows.Changes;
using Microsoft.Win32;

namespace Blocker.Platform.Windows.Tests.Changes;

/// <summary>In-memory IFEO registry. <see cref="SharedKey"/> mimics Windows sharing one key between both views.</summary>
internal sealed class FakeIfeoStore : IIfeoStore
{
    private readonly Dictionary<(IfeoView, string), Dictionary<string, string>> _keys = [];

    public bool SharedKey { get; init; }

    public Dictionary<string, string>? Values(IfeoView view, string image) =>
        _keys.TryGetValue(Key(view, image), out Dictionary<string, string>? values) ? values : null;

    public void Seed(IfeoView view, string image, params (string Name, string Value)[] values) =>
        _keys[Key(view, image)] = values.ToDictionary(v => v.Name, v => v.Value);

    public IfeoKeySnapshot? Read(IfeoView view, string imageName) =>
        Values(view, imageName) is { } v
            ? new IfeoKeySnapshot(v.GetValueOrDefault("Debugger"), v.GetValueOrDefault(PlatformMarker.MarkerValueName))
            : null;

    public void Write(IfeoView view, string imageName, string? debugger, string? owner)
    {
        if (!_keys.TryGetValue(Key(view, imageName), out Dictionary<string, string>? values))
        {
            _keys[Key(view, imageName)] = values = [];
        }

        Set(values, PlatformMarker.MarkerValueName, owner);
        Set(values, "Debugger", debugger);
    }

    public bool DeleteKeyIfEmpty(IfeoView view, string imageName)
    {
        if (Values(view, imageName) is { Count: 0 })
        {
            _keys.Remove(Key(view, imageName));
            return true;
        }

        return false;
    }

    public IReadOnlyList<string> ListImages(IfeoView view) =>
        [.. _keys.Keys.Where(k => k.Item1 == Normalize(view)).Select(k => k.Item2)];

    private static void Set(Dictionary<string, string> values, string name, string? value)
    {
        if (value is null)
        {
            values.Remove(name);
        }
        else
        {
            values[name] = value;
        }
    }

    private (IfeoView, string) Key(IfeoView view, string image) => (Normalize(view), image);

    private IfeoView Normalize(IfeoView view) => SharedKey ? IfeoView.Native : view;
}

/// <summary>In-memory policy registry with real key-chain semantics (case-insensitive).</summary>
internal sealed class FakePolicyStore : IRegistryPolicyStore
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Key, string Name), PolicyValue> _values = new();

    public void SeedKey(string keyPath) => CreateChain(keyPath);

    public void Seed(string keyPath, string name, PolicyValue value)
    {
        CreateChain(keyPath);
        _values[(keyPath.ToUpperInvariant(), name.ToUpperInvariant())] = value;
    }

    public bool KeyExists(string keyPath) => _keys.Contains(keyPath);

    public PolicyValue? Get(string keyPath, string name) => ReadValue(keyPath, name);

    public int CountExistingSegments(string keyPath)
    {
        string[] segments = keyPath.Split('\\');
        for (int length = segments.Length; length > 0; length--)
        {
            if (_keys.Contains(string.Join('\\', segments[..length])))
            {
                return length;
            }
        }

        return 0;
    }

    public PolicyValue? ReadValue(string keyPath, string name) =>
        _values.GetValueOrDefault((keyPath.ToUpperInvariant(), name.ToUpperInvariant()));

    public void WriteValue(string keyPath, string name, PolicyValue value) => Seed(keyPath, name, value);

    public void DeleteValue(string keyPath, string name) => _values.Remove((keyPath.ToUpperInvariant(), name.ToUpperInvariant()));

    public bool DeleteKeyIfEmpty(string keyPath)
    {
        string upper = keyPath.ToUpperInvariant();
        bool hasValues = _values.Keys.Any(k => k.Key == upper);
        bool hasChildren = _keys.Any(k => k.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase));
        return _keys.Contains(keyPath) && !hasValues && !hasChildren && _keys.Remove(keyPath);
    }

    public IReadOnlyList<PolicyMarker> FindMarkers(string rootKeyPath) =>
    [
        .. _values
            .Where(v => v.Key.Name.StartsWith(RegistryPolicyChangeHandler.MarkerPrefix.ToUpperInvariant(), StringComparison.Ordinal)
                && (v.Key.Key == rootKeyPath.ToUpperInvariant() || v.Key.Key.StartsWith(rootKeyPath.ToUpperInvariant() + "\\", StringComparison.Ordinal)))
            .Select(v => new PolicyMarker(
                _keys.First(k => string.Equals(k, v.Key.Key, StringComparison.OrdinalIgnoreCase)),
                v.Key.Name[RegistryPolicyChangeHandler.MarkerPrefix.Length..],
                v.Value.Text!)),
    ];

    private void CreateChain(string keyPath)
    {
        string[] segments = keyPath.Split('\\');
        for (int length = 1; length <= segments.Length; length++)
        {
            _keys.Add(string.Join('\\', segments[..length]));
        }
    }
}

/// <summary>In-memory service DACLs.</summary>
internal sealed class FakeServiceSecurity : IServiceSecurity
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

/// <summary>In-memory directory DACLs recording every write.</summary>
internal sealed class FakeDirectoryAcl : IDirectoryAclStore
{
    public Dictionary<string, DirectoryAclSnapshot> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, string Sddl)> Writes { get; } = [];

    public void Add(string path, string sddl, bool reparse = false) => Directories[path] = new DirectoryAclSnapshot(reparse, sddl);

    public DirectoryAclSnapshot? Read(string path) => Directories.GetValueOrDefault(path);

    public void WriteSddl(string path, string sddl)
    {
        Writes.Add((path, sddl));
        Directories[path] = Directories[path] with { Sddl = sddl };
    }
}

/// <summary>A disposable that records when it is disposed.</summary>
internal sealed class FakeWfpSession(List<string> log, string name) : IDisposable
{
    public void Dispose() => log.Add($"dispose {name}");
}

/// <summary>Helpers to build registry policy values in tests.</summary>
internal static class PolicyValues
{
    public static PolicyValue Multi(params string[] lines) => new(RegistryValueKind.MultiString, Lines: lines);
}

/// <summary>Text-based stand-in for the Windows-only ACL transform: "adding" appends a tag, "removing" strips every tag.</summary>
internal sealed class FakeDenyTransform : IDenyAclTransform
{
    public const string Tag = "#DENY";

    public string AddDenyAces(string sddl) => HasDenyAces(sddl) ? sddl : sddl + Tag;

    public (string Sddl, int Removed) RemoveDenyAces(string sddl)
    {
        int count = (sddl.Length - sddl.Replace(Tag, string.Empty, StringComparison.Ordinal).Length) / Tag.Length;
        return (sddl.Replace(Tag, string.Empty, StringComparison.Ordinal), count);
    }

    public bool HasDenyAces(string sddl) => sddl.Contains(Tag, StringComparison.Ordinal);
}
