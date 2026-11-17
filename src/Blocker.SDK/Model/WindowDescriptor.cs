namespace Blocker.Sdk.Model;

/// <summary>
/// Describes the foreground window of the interactive user session.
/// </summary>
/// <param name="ProcessId">Id of the process that owns the window.</param>
/// <param name="ImageName">File name of the owning executable.</param>
/// <param name="ExecutablePath">Full path of the owning executable.</param>
/// <param name="PackageFamilyName">Package family name for Store apps; otherwise <see langword="null"/>.</param>
/// <param name="Title">
/// Window title. Always <see langword="null"/> unless the plugin holds <see cref="PluginCapability.WindowTitles"/>.
/// </param>
public sealed record WindowDescriptor(
    int ProcessId,
    string ImageName,
    string ExecutablePath,
    string? PackageFamilyName,
    string? Title);
