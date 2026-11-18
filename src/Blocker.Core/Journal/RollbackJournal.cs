using System.Text.Json;
using Blocker.Core.Security;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Journal;

/// <summary>
/// Write-ahead rollback journal: intent and original state are durable before every change, rollback replays in reverse
/// and is idempotent. A damaged journal falls back to a <c>BlockerOwned=installId</c> marker sweep.
/// </summary>
public sealed class RollbackJournal(
    SecureFileStore store,
    string journalPath,
    IEnumerable<IReversibleChangeHandler> handlers,
    string installId,
    ILogger<RollbackJournal> logger)
{
    private const int FormatVersion = 1;
    private readonly Dictionary<string, IReversibleChangeHandler> _handlers = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Records <paramref name="desiredStateJson"/> for <paramref name="target"/> durably, then applies it.</summary>
    /// <exception cref="ArgumentException">No handler is registered for <paramref name="kind"/>.</exception>
    /// <exception cref="InvalidOperationException">The existing journal is damaged; run <see cref="RollbackAllAsync"/> first.</exception>
    public async ValueTask ApplyAsync(string kind, string target, string desiredStateJson, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(kind, out IReversibleChangeHandler? handler))
        {
            throw new ArgumentException($"No handler registered for kind '{kind}'.", nameof(kind));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<JournalEntry> entries = await LoadForApplyAsync(cancellationToken).ConfigureAwait(false);
            string? current = await handler.CaptureOriginalAsync(target, cancellationToken).ConfigureAwait(false);

            // A target already changed by this journal keeps its first original, never an already-modified state.
            JournalEntry? first = entries.Find(e => e.Kind == kind && e.Target == target && e.State != JournalEntryState.RolledBack);
            long sequence = entries.Count == 0 ? 1 : entries.Max(e => e.Sequence) + 1;
            var entry = new JournalEntry(sequence, Guid.NewGuid(), kind, target, first is null ? current : first.OriginalStateJson, desiredStateJson, JournalEntryState.Pending);
            entries.Add(entry);
            await PersistAsync(entries, cancellationToken).ConfigureAwait(false);

            try
            {
                await handler.ApplyAsync(target, desiredStateJson, cancellationToken).ConfigureAwait(false);
                entries[^1] = entry with { State = JournalEntryState.Applied };
                await PersistAsync(entries, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await TryUndoFailedApplyAsync(handler, entries, current, ex).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reverts every Pending and Applied change (reverse order), or sweeps by marker when the journal is damaged.</summary>
    public async ValueTask<RollbackReport> RollbackAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<JournalEntry>? entries;
            try
            {
                entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TamperDetectedException or JsonException)
            {
                logger.LogError(ex, "Rollback journal {Path} is damaged; falling back to marker sweep.", journalPath);
                return await SweepAsync(cancellationToken).ConfigureAwait(false);
            }

            if (entries is null)
            {
                return new RollbackReport(0, [], RollbackMode.JournalReplay, true);
            }

            int restored = 0;
            List<RollbackFailure> failures = [];
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                JournalEntry entry = entries[i];
                if (entry.State == JournalEntryState.RolledBack)
                {
                    continue;
                }

                try
                {
                    await RestoreEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    entries[i] = entry with { State = JournalEntryState.RolledBack };
                    await PersistAsync(entries, cancellationToken).ConfigureAwait(false);
                    restored++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Rollback of {Kind} '{Target}' (#{Sequence}) failed.", entry.Kind, entry.Target, entry.Sequence);
                    failures.Add(new RollbackFailure(entry, ex.Message));
                }
            }

            if (failures.Count == 0)
            {
                store.Delete(journalPath);
            }

            return new RollbackReport(restored, failures, RollbackMode.JournalReplay, failures.Count == 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes every artifact that carries this installation's <c>BlockerOwned</c> marker, whatever the journal says (uninstall and offline
    /// recovery run it after <see cref="RollbackAllAsync"/> as a safety net). The journal file is deleted when every sweep succeeded.
    /// </summary>
    public async ValueTask<RollbackReport> SweepAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SweepAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when changes are not fully reverted, or the journal is damaged and needs a sweep.</summary>
    public async ValueTask<bool> HasPendingChangesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<JournalEntry>? entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return entries is not null && entries.Exists(e => e.State != JournalEntryState.RolledBack);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Snapshot of the journal ordered by sequence (empty when no journal exists).</summary>
    /// <exception cref="TamperDetectedException">The journal failed verification.</exception>
    public async ValueTask<IReadOnlyList<JournalEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false) ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<List<JournalEntry>?> LoadAsync(CancellationToken cancellationToken)
    {
        JournalDocument? document = await store.ReadJsonAsync(journalPath, JournalJsonContext.Default.JournalDocument, cancellationToken).ConfigureAwait(false);
        return document?.Entries.OrderBy(e => e.Sequence).ToList();
    }

    private async ValueTask<List<JournalEntry>> LoadForApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            throw new InvalidOperationException("The rollback journal is damaged; run a rollback (marker sweep) before applying new changes.", ex);
        }
    }

    private ValueTask PersistAsync(List<JournalEntry> entries, CancellationToken cancellationToken) =>
        store.WriteJsonAsync(journalPath, new JournalDocument(FormatVersion, entries), JournalJsonContext.Default.JournalDocument, cancellationToken);

    private ValueTask RestoreEntryAsync(JournalEntry entry, CancellationToken cancellationToken) =>
        _handlers.TryGetValue(entry.Kind, out IReversibleChangeHandler? handler)
            ? handler.RestoreAsync(entry.Target, entry.OriginalStateJson, cancellationToken)
            : throw new InvalidOperationException($"No handler registered for kind '{entry.Kind}'.");

    /// <summary>Best effort and uncancellable: on failure the entry stays Pending so a later rollback replays it.</summary>
    private async ValueTask TryUndoFailedApplyAsync(IReversibleChangeHandler handler, List<JournalEntry> entries, string? stateBeforeApply, Exception cause)
    {
        JournalEntry entry = entries[^1];
        logger.LogWarning(cause, "Applying {Kind} '{Target}' failed; restoring the previous state.", entry.Kind, entry.Target);
        try
        {
            await handler.RestoreAsync(entry.Target, stateBeforeApply, CancellationToken.None).ConfigureAwait(false);
            entries[^1] = entry with { State = JournalEntryState.RolledBack };
            await PersistAsync(entries, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception restoreFailure)
        {
            logger.LogError(restoreFailure, "Restoring {Kind} '{Target}' after a failed apply also failed; the entry stays Pending.", entry.Kind, entry.Target);
        }
    }

    private async ValueTask<RollbackReport> SweepAsync(CancellationToken cancellationToken)
    {
        int removed = 0;
        List<RollbackFailure> failures = [];
        foreach (IReversibleChangeHandler handler in _handlers.Values)
        {
            try
            {
                removed += await handler.SweepAsync(installId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Marker sweep for kind {Kind} failed.", handler.Kind);
                failures.Add(new RollbackFailure(null, $"{handler.Kind}: {ex.Message}"));
            }
        }

        if (failures.Count == 0)
        {
            store.Delete(journalPath);
        }
        else
        {
            logger.LogError("Damaged journal {Path} kept: {Count} marker sweep(s) failed.", journalPath, failures.Count);
        }

        return new RollbackReport(removed, failures, RollbackMode.MarkerSweep, failures.Count == 0);
    }
}
