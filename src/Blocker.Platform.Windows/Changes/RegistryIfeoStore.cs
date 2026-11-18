using Microsoft.Win32;

namespace Blocker.Platform.Windows.Changes;

/// <summary>Registry-backed <see cref="IIfeoStore"/> (HKLM by default; the hive and path are injectable for tests).</summary>
internal sealed class RegistryIfeoStore(RegistryHive hive, string basePath) : IIfeoStore
{
    /// <summary>Location of the IFEO key below HKLM.</summary>
    internal const string DefaultBasePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private const string DebuggerValue = "Debugger";

    public RegistryIfeoStore()
        : this(RegistryHive.LocalMachine, DefaultBasePath)
    {
    }

    public IfeoKeySnapshot? Read(IfeoView view, string imageName)
    {
        using RegistryKey root = Open(view);
        using RegistryKey? key = root.OpenSubKey(KeyPath(imageName), writable: false);
        return key is null
            ? null
            : new IfeoKeySnapshot(
                key.GetValue(DebuggerValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                key.GetValue(PlatformMarker.MarkerValueName) as string);
    }

    public void Write(IfeoView view, string imageName, string? debugger, string? owner)
    {
        using RegistryKey root = Open(view);
        using RegistryKey key = root.CreateSubKey(KeyPath(imageName), writable: true);
        if (owner is not null)
        {
            key.SetValue(PlatformMarker.MarkerValueName, owner, RegistryValueKind.String);
        }

        if (debugger is not null)
        {
            key.SetValue(DebuggerValue, debugger, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(DebuggerValue, throwOnMissingValue: false);
        }

        if (owner is null)
        {
            key.DeleteValue(PlatformMarker.MarkerValueName, throwOnMissingValue: false);
        }
    }

    public bool DeleteKeyIfEmpty(IfeoView view, string imageName)
    {
        using RegistryKey root = Open(view);
        using (RegistryKey? key = root.OpenSubKey(KeyPath(imageName), writable: false))
        {
            if (key is null || key.SubKeyCount != 0 || key.ValueCount != 0)
            {
                return false;
            }
        }

        root.DeleteSubKey(KeyPath(imageName), throwOnMissingSubKey: false);
        return true;
    }

    public IReadOnlyList<string> ListImages(IfeoView view)
    {
        using RegistryKey root = Open(view);
        using RegistryKey? key = root.OpenSubKey(basePath, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    private RegistryKey Open(IfeoView view) =>
        RegistryKey.OpenBaseKey(hive, view == IfeoView.Native ? RegistryView.Registry64 : RegistryView.Registry32);

    private string KeyPath(string imageName) => $@"{basePath}\{imageName}";
}
