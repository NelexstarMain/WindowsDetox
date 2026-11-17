namespace Blocker.Core.Journal;

/// <summary>Applies and reverts one kind of system change (IFEO, service DACL, ACL deny ACE, browser policy, ...).</summary>
public interface IReversibleChangeHandler
{
    /// <summary>Stable discriminator stored in the journal; unique per handler.</summary>
    string Kind { get; }

    /// <summary>Returns the current state of <paramref name="target"/> as JSON, or null when it does not exist.</summary>
    ValueTask<string?> CaptureOriginalAsync(string target, CancellationToken cancellationToken);

    /// <summary>Makes <paramref name="target"/> match <paramref name="desiredStateJson"/>, tagging every artifact with <c>BlockerOwned=installId</c>.</summary>
    ValueTask ApplyAsync(string target, string desiredStateJson, CancellationToken cancellationToken);

    /// <summary>Returns <paramref name="target"/> to <paramref name="originalStateJson"/> (null: remove it). Must be idempotent.</summary>
    ValueTask RestoreAsync(string target, string? originalStateJson, CancellationToken cancellationToken);

    /// <summary>Removes every artifact carrying the <c>BlockerOwned=installId</c> marker; returns how many were removed.</summary>
    ValueTask<int> SweepAsync(string installId, CancellationToken cancellationToken);
}
