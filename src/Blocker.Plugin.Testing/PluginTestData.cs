using Blocker.Sdk.Model;
using Blocker.Sdk.Session;

namespace Blocker.Plugin.Testing;

/// <summary>Deterministic factories for SDK data objects used in plugin tests.</summary>
public static class PluginTestData
{
    private static readonly Guid TestSessionId = new("00000000-0000-0000-0000-00000000d0e5");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a process located at <c>C:\Games\{imageName}</c>, started at 2026-01-01T09:00:00Z.</summary>
    /// <param name="imageName">Executable file name.</param>
    /// <param name="pid">Process id.</param>
    /// <returns>The descriptor; parent id is 1, hash and package family are <see langword="null"/>.</returns>
    public static ProcessDescriptor Process(string imageName = "game.exe", int pid = 4242)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        return new ProcessDescriptor(pid, 1, imageName, $@"C:\Games\{imageName}", null, null, Start);
    }

    /// <summary>Creates a snapshot of a running session with two emergency passes per day of five minutes each.</summary>
    /// <param name="remaining">Time until the session ends.</param>
    /// <param name="passesLeft">Emergency passes left today.</param>
    /// <returns>An <see cref="SessionPhase.Active"/> snapshot.</returns>
    public static SessionSnapshot ActiveSession(TimeSpan remaining, int passesLeft = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(remaining, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(passesLeft);
        return new SessionSnapshot(
            SessionPhase.Active, TestSessionId, remaining, null, passesLeft, Math.Max(2, passesLeft), TimeSpan.FromMinutes(5));
    }
}
