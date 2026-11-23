using System.IO.Abstractions;
using System.Runtime.Versioning;

namespace Blocker.Platform.Windows.Changes;

/// <summary>The install-time plugin data directory: created with its security at <c>--install</c>, removed at <c>--uninstall</c>.</summary>
public interface IPluginDataDirectory
{
    /// <summary>Creates <paramref name="path"/> (if missing) and applies the plugin data security descriptor.</summary>
    /// <param name="path">Directory path.</param>
    void Provision(string path);

    /// <summary>Deletes <paramref name="path"/> with everything in it; a missing directory is not an error.</summary>
    /// <param name="path">Directory path.</param>
    void Remove(string path);
}

/// <summary>
/// The security descriptor of the plugin data directory. The Low mandatory label lets the low-integrity plugin host write
/// (the label's <c>NW</c> policy only blocks writes from <i>lower</i> levels, and Low is the lowest). The DACL is protected, so nothing is
/// inherited from <c>ProgramData</c>: SYSTEM and Administrators get full control, and the Users group gets modify rights, which is how
/// the restricted host's token (the console user's, with Administrators deny-only) reaches the directory.
/// </summary>
public static class PluginDataSecurity
{
    /// <summary>Protected DACL (SYSTEM full, Administrators full, Users modify) plus the Low mandatory integrity label, both inherited by children.</summary>
    public const string Sddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301BF;;;BU)S:(ML;OICI;NW;;;LW)";
}

/// <summary>Real <see cref="IPluginDataDirectory"/> using <c>SetNamedSecurityInfo</c>; needs an elevated administrator.</summary>
/// <param name="fileSystem">File system used to create and delete the directory.</param>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class PluginDataDirectory(IFileSystem fileSystem) : IPluginDataDirectory
{
    /// <inheritdoc />
    public void Provision(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = fileSystem.Directory.CreateDirectory(path);
        DirectorySddl.Apply(path, PluginDataSecurity.Sddl, includeLabel: true);
    }

    /// <inheritdoc />
    public void Remove(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (fileSystem.Directory.Exists(path))
        {
            fileSystem.Directory.Delete(path, recursive: true);
        }
    }
}
