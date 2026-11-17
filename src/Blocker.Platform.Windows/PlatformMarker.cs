namespace Blocker.Platform.Windows;

/// <summary>Constants shared by Windows implementations.</summary>
public static class PlatformMarker
{
    /// <summary>Registry value / ACE marker name written on every artifact Blocker creates, so rollback sweeps can find them.</summary>
    public const string MarkerValueName = "BlockerOwned";
}
