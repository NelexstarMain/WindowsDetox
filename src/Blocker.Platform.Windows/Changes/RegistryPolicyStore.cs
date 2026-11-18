using Microsoft.Win32;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Registry-backed <see cref="IRegistryPolicyStore"/> (64-bit view; <c>SOFTWARE\Policies</c> is shared with WOW64).</summary>
internal sealed class RegistryPolicyStore(RegistryHive hive) : IRegistryPolicyStore
{
    public int CountExistingSegments(string keyPath)
    {
        using RegistryKey root = Open();
        string[] segments = keyPath.Split('\\');
        for (int length = segments.Length; length > 0; length--)
        {
            using RegistryKey? key = root.OpenSubKey(string.Join('\\', segments[..length]), writable: false);
            if (key is not null)
            {
                return length;
            }
        }

        return 0;
    }

    public PolicyValue? ReadValue(string keyPath, string name)
    {
        using RegistryKey root = Open();
        using RegistryKey? key = root.OpenSubKey(keyPath, writable: false);
        return key is null ? null : ReadFrom(key, name);
    }

    public void WriteValue(string keyPath, string name, PolicyValue value)
    {
        using RegistryKey root = Open();
        using RegistryKey key = root.CreateSubKey(keyPath, writable: true);
        switch (value.Kind)
        {
            case RegistryValueKind.String or RegistryValueKind.ExpandString:
                key.SetValue(name, value.Text ?? string.Empty, value.Kind);
                break;
            case RegistryValueKind.MultiString:
                key.SetValue(name, value.Lines ?? [], RegistryValueKind.MultiString);
                break;
            case RegistryValueKind.DWord:
                key.SetValue(name, unchecked((int)(uint)(value.Number ?? 0)), RegistryValueKind.DWord);
                break;
            case RegistryValueKind.QWord:
                key.SetValue(name, value.Number ?? 0L, RegistryValueKind.QWord);
                break;
            default:
                key.SetValue(name, value.Bytes ?? [], value.Kind == RegistryValueKind.None ? RegistryValueKind.None : RegistryValueKind.Binary);
                break;
        }
    }

    public void DeleteValue(string keyPath, string name)
    {
        using RegistryKey root = Open();
        using RegistryKey? key = root.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool DeleteKeyIfEmpty(string keyPath)
    {
        using RegistryKey root = Open();
        using (RegistryKey? key = root.OpenSubKey(keyPath, writable: false))
        {
            if (key is null || key.SubKeyCount != 0 || key.ValueCount != 0)
            {
                return false;
            }
        }

        root.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
        return true;
    }

    public IReadOnlyList<PolicyMarker> FindMarkers(string rootKeyPath)
    {
        using RegistryKey root = Open();
        List<PolicyMarker> markers = [];
        Collect(root, rootKeyPath, markers);
        return markers;
    }

    private static void Collect(RegistryKey root, string keyPath, List<PolicyMarker> markers)
    {
        using RegistryKey? key = root.OpenSubKey(keyPath, writable: false);
        if (key is null)
        {
            return;
        }

        foreach (string valueName in key.GetValueNames())
        {
            if (valueName.StartsWith(RegistryPolicyChangeHandler.MarkerPrefix, StringComparison.OrdinalIgnoreCase)
                && key.GetValue(valueName) is string owner)
            {
                markers.Add(new PolicyMarker(keyPath, valueName[RegistryPolicyChangeHandler.MarkerPrefix.Length..], owner));
            }
        }

        foreach (string sub in key.GetSubKeyNames())
        {
            Collect(root, $@"{keyPath}\{sub}", markers);
        }
    }

    private static PolicyValue? ReadFrom(RegistryKey key, string name)
    {
        object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return null;
        }

        RegistryValueKind kind = key.GetValueKind(name);
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => new PolicyValue(kind, Text: (string)raw),
            RegistryValueKind.MultiString => new PolicyValue(kind, Lines: (string[])raw),
            RegistryValueKind.DWord => new PolicyValue(kind, Number: unchecked((uint)(int)raw)),
            RegistryValueKind.QWord => new PolicyValue(kind, Number: (long)raw),
            RegistryValueKind.None => new PolicyValue(kind, Bytes: (byte[])raw),
            _ => new PolicyValue(RegistryValueKind.Binary, Bytes: (byte[])raw),
        };
    }

    private RegistryKey Open() => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
}
