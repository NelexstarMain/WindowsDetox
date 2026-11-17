namespace Blocker.Sdk.Model;

/// <summary>
/// Describes a process observed by the enforcement engine.
/// </summary>
/// <param name="ProcessId">Operating-system process id. Ids are reused, so pair it with <paramref name="StartedAt"/>.</param>
/// <param name="ParentProcessId">Process id of the creator process, or 0 when unknown.</param>
/// <param name="ImageName">File name of the executable, for example <c>chrome.exe</c>.</param>
/// <param name="ExecutablePath">Full, normalized path of the executable.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the executable, when already computed.</param>
/// <param name="PackageFamilyName">Microsoft Store package family name for packaged apps; otherwise <see langword="null"/>.</param>
/// <param name="StartedAt">Verified time at which the process started.</param>
public sealed record ProcessDescriptor(
    int ProcessId,
    int ParentProcessId,
    string ImageName,
    string ExecutablePath,
    string? Sha256,
    string? PackageFamilyName,
    DateTimeOffset StartedAt);
