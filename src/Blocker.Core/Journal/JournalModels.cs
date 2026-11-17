using System.Text.Json.Serialization;

namespace Blocker.Core.Journal;

/// <summary>Lifecycle of a journaled change.</summary>
public enum JournalEntryState
{
    /// <summary>Intent recorded; it is unknown whether the change was applied.</summary>
    Pending,

    /// <summary>The change was applied.</summary>
    Applied,

    /// <summary>The original state was restored.</summary>
    RolledBack,
}

/// <summary>One write-ahead journal record.</summary>
public sealed record JournalEntry(
    long Sequence,
    Guid OperationId,
    string Kind,
    string Target,
    string? OriginalStateJson,
    string DesiredStateJson,
    JournalEntryState State);

/// <summary>How a rollback located the changes to revert.</summary>
public enum RollbackMode
{
    /// <summary>The verified journal was replayed in reverse.</summary>
    JournalReplay,

    /// <summary>The journal was unusable; every handler swept its <c>BlockerOwned</c> artifacts.</summary>
    MarkerSweep,
}

/// <summary>A rollback step that failed; <see cref="Entry"/> is null for a failed marker sweep.</summary>
public sealed record RollbackFailure(JournalEntry? Entry, string Message);

/// <summary>Outcome of <see cref="RollbackJournal.RollbackAllAsync"/>.</summary>
public sealed record RollbackReport(int Restored, IReadOnlyList<RollbackFailure> Failures, RollbackMode Mode, bool Complete);

internal sealed record JournalDocument(int Version, List<JournalEntry> Entries);

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalDocument))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
