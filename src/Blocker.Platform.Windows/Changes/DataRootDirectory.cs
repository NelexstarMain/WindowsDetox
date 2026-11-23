using System.Runtime.Versioning;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// The security descriptors of the data root (<c>%ProgramData%\WindowsDetox</c>). It holds the DPAPI-protected HMAC key and every sealed state file,
/// including the rollback journal that is replayed as SYSTEM, so a standard user must neither read nor create anything in it. <c>ProgramData</c>
/// would otherwise hand <c>Users</c> read and create rights by inheritance. Both DACLs are protected (nothing is inherited).
/// </summary>
public static class DataRootSecurity
{
    /// <summary>
    /// SYSTEM and Administrators get full control, inherited by children. <c>Users</c> only list and traverse the directory itself (no inheritance,
    /// so no file in it is readable) to reach the plugins directory on tokens without the bypass-traverse privilege.
    /// </summary>
    public const string RootSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200A9;;;BU)";

    /// <summary>The installed-plugins directory: like the root, plus read and execute for Users, which is how the restricted plugin host reads plugin sources.</summary>
    public const string PluginsSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200A9;;;BU)";
}

/// <summary>Real data root provisioning using <c>SetNamedSecurityInfo</c>; needs an elevated administrator or SYSTEM.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class DataRootDirectory
{
    /// <summary>
    /// Creates the data root and the plugins directory (if missing) and applies <see cref="DataRootSecurity"/>. Idempotent and cheap, so it runs at
    /// <c>--install</c> and at every service start; that also migrates installations created before the DACL existed (the inheritable ACEs are
    /// propagated to the files already inside, replacing the ones inherited from <c>ProgramData</c>). The root goes first so the plugins directory,
    /// which has its own protected DACL, keeps it.
    /// </summary>
    /// <param name="dataRoot">The data root.</param>
    /// <param name="pluginsDirectory">The installed-plugins directory inside the data root.</param>
    public static void Protect(string dataRoot, string pluginsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        _ = Directory.CreateDirectory(dataRoot);
        DirectorySddl.Apply(dataRoot, DataRootSecurity.RootSddl, includeLabel: false);
        _ = Directory.CreateDirectory(pluginsDirectory);
        DirectorySddl.Apply(pluginsDirectory, DataRootSecurity.PluginsSddl, includeLabel: false);
    }
}
