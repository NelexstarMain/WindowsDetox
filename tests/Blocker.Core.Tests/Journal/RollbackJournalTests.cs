using Blocker.Core.Journal;
using Blocker.Core.Security;
using Blocker.Core.Tests.Security;

namespace Blocker.Core.Tests.Journal;

public sealed class RollbackJournalTests
{
    private readonly JournalHarness _h = new();

    private FakeSystem Sys => _h.System;

    [Fact]
    public async Task Apply_then_rollback_restores_system_state_exactly()
    {
        Sys.Values["a"] = "1";
        RollbackJournal journal = _h.NewJournal();

        await journal.ApplyAsync("fake", "a", "2", _h.Ct);
        await journal.ApplyAsync("fake", "b", "x", _h.Ct);
        Assert.Equal([("a", "2"), ("b", "x")], Sys.Values.Select(p => (p.Key, p.Value)));
        Assert.True(await journal.HasPendingChangesAsync(_h.Ct));

        RollbackReport report = await journal.RollbackAllAsync(_h.Ct);

        Assert.Equal(2, report.Restored);
        Assert.Empty(report.Failures);
        Assert.Equal(RollbackMode.JournalReplay, report.Mode);
        Assert.True(report.Complete);
        Assert.Equal([("a", "1")], Sys.Values.Select(p => (p.Key, p.Value)));
        Assert.Empty(Sys.Owners);
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
        Assert.False(await journal.HasPendingChangesAsync(_h.Ct));
    }

    [Fact]
    public async Task Entries_record_sequence_originals_and_applied_state()
    {
        Sys.Values["a"] = "1";
        RollbackJournal journal = _h.NewJournal();

        await journal.ApplyAsync("fake", "a", "2", _h.Ct);
        await journal.ApplyAsync("fake", "b", "x", _h.Ct);

        IReadOnlyList<JournalEntry> entries = await journal.GetEntriesAsync(_h.Ct);
        Assert.Equal([1L, 2L], entries.Select(e => e.Sequence));
        Assert.Equal(["a", "b"], entries.Select(e => e.Target));
        Assert.Equal(["1", null], entries.Select(e => e.OriginalStateJson));
        Assert.Equal(["2", "x"], entries.Select(e => e.DesiredStateJson));
        Assert.All(entries, e => Assert.Equal(JournalEntryState.Applied, e.State));
        Assert.All(entries, e => Assert.Equal("fake", e.Kind));
        Assert.Equal(2, entries.Select(e => e.OperationId).Distinct().Count());
    }

    [Fact]
    public async Task Pending_entry_with_original_is_durable_before_the_change_happens()
    {
        Sys.Values["a"] = "1";
        IReadOnlyList<JournalEntry>? seenDuringApply = null;
        Sys.OnApply = _ => seenDuringApply = _h.NewJournal().GetEntriesAsync(_h.Ct).AsTask().GetAwaiter().GetResult();

        await _h.NewJournal().ApplyAsync("fake", "a", "2", _h.Ct);

        JournalEntry entry = Assert.Single(seenDuringApply!);
        Assert.Equal(JournalEntryState.Pending, entry.State);
        Assert.Equal("1", entry.OriginalStateJson);
        Assert.Equal("2", entry.DesiredStateJson);
    }

    [Fact]
    public async Task Unknown_kind_is_rejected_without_touching_anything()
    {
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(async () => await _h.NewJournal().ApplyAsync("nope", "a", "2", _h.Ct));

        Assert.Equal("kind", ex.ParamName);
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
        Assert.Empty(Sys.Values);
    }

    [Fact]
    public async Task Apply_failure_restores_marks_rolled_back_and_rethrows()
    {
        Sys.Values["a"] = "1";
        Sys.ApplyFault = _ =>
        {
            Sys.Values["a"] = "half-written";
            return new InvalidOperationException("apply boom");
        };
        RollbackJournal journal = _h.NewJournal();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await journal.ApplyAsync("fake", "a", "2", _h.Ct));

        Assert.Equal("apply boom", ex.Message);
        Assert.Equal([("a", "1")], Sys.Values.Select(p => (p.Key, p.Value)));
        JournalEntry entry = Assert.Single(await journal.GetEntriesAsync(_h.Ct));
        Assert.Equal(JournalEntryState.RolledBack, entry.State);
        Assert.False(await journal.HasPendingChangesAsync(_h.Ct));
    }

    [Fact]
    public async Task Failed_second_apply_restores_the_state_before_it_not_the_first_original()
    {
        Sys.Values["a"] = "orig";
        RollbackJournal journal = _h.NewJournal();
        await journal.ApplyAsync("fake", "a", "v1", _h.Ct);
        Sys.ApplyFault = _ => new InvalidOperationException("second apply boom");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await journal.ApplyAsync("fake", "a", "v2", _h.Ct));

        Assert.Equal("v1", Sys.Values["a"]);
        Assert.Equal([JournalEntryState.Applied, JournalEntryState.RolledBack], (await journal.GetEntriesAsync(_h.Ct)).Select(e => e.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crash_during_apply_is_recovered_by_a_new_journal_instance(bool changeLandedBeforeCrash)
    {
        Sys.Values["a"] = "orig";
        Sys.ApplyFault = target =>
        {
            if (changeLandedBeforeCrash)
            {
                Sys.Values[target] = "half-applied";
                Sys.Owners[target] = JournalHarness.InstallId;
            }

            Sys.PowerLost = true;
            return new PowerLossException();
        };
        Sys.RestoreFault = _ => Sys.PowerLost ? new PowerLossException() : null;

        await Assert.ThrowsAsync<PowerLossException>(async () => await _h.NewJournal().ApplyAsync("fake", "a", "new", _h.Ct));

        // Process "died": the dead process could not restore, the entry stays Pending in the journal file.
        RollbackJournal afterReboot = _h.NewJournal();
        JournalEntry pending = Assert.Single(await afterReboot.GetEntriesAsync(_h.Ct));
        Assert.Equal(JournalEntryState.Pending, pending.State);
        Assert.Equal("orig", pending.OriginalStateJson);
        Assert.True(await afterReboot.HasPendingChangesAsync(_h.Ct));
        Assert.Equal(changeLandedBeforeCrash ? "half-applied" : "orig", Sys.Values["a"]);

        Sys.PowerLost = false;
        Sys.ApplyFault = null;
        RollbackReport report = await afterReboot.RollbackAllAsync(_h.Ct);

        Assert.Equal(1, report.Restored);
        Assert.True(report.Complete);
        Assert.Equal([("a", "orig")], Sys.Values.Select(p => (p.Key, p.Value)));
        Assert.Empty(Sys.Owners);
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
    }

    [Fact]
    public async Task Rollback_runs_in_reverse_order_so_dependent_changes_unwind_cleanly()
    {
        Sys.RestoreFault = target => target == "svc" && Sys.Values.ContainsKey("svc/rule") ? new InvalidOperationException("child still present") : null;
        RollbackJournal journal = _h.NewJournal();
        await journal.ApplyAsync("fake", "svc", "created", _h.Ct);
        await journal.ApplyAsync("fake", "svc/rule", "deny", _h.Ct);
        await journal.ApplyAsync("fake", "svc/acl", "ace", _h.Ct);

        RollbackReport report = await journal.RollbackAllAsync(_h.Ct);

        Assert.Equal(["svc/acl", "svc/rule", "svc"], Sys.RestoreLog);
        Assert.Equal(3, report.Restored);
        Assert.True(report.Complete);
        Assert.Empty(Sys.Values);
    }

    [Fact]
    public async Task Applying_the_same_target_twice_keeps_the_first_original()
    {
        Sys.Values["a"] = "orig";
        RollbackJournal journal = _h.NewJournal();

        await journal.ApplyAsync("fake", "a", "v1", _h.Ct);
        await journal.ApplyAsync("fake", "a", "v2", _h.Ct);

        IReadOnlyList<JournalEntry> entries = await journal.GetEntriesAsync(_h.Ct);
        Assert.Equal(["orig", "orig"], entries.Select(e => e.OriginalStateJson));
        await journal.RollbackAllAsync(_h.Ct);
        Assert.Equal([("a", "orig")], Sys.Values.Select(p => (p.Key, p.Value)));
    }

    [Fact]
    public async Task Applying_a_new_target_twice_still_rolls_back_to_absent()
    {
        RollbackJournal journal = _h.NewJournal();

        await journal.ApplyAsync("fake", "a", "v1", _h.Ct);
        await journal.ApplyAsync("fake", "a", "v2", _h.Ct);

        Assert.Equal([null, null], (await journal.GetEntriesAsync(_h.Ct)).Select(e => e.OriginalStateJson));
        await journal.RollbackAllAsync(_h.Ct);
        Assert.Empty(Sys.Values);
    }

    [Fact]
    public async Task Partial_failure_continues_reports_and_keeps_the_journal()
    {
        Sys.Values["a"] = "1";
        Sys.Values["b"] = "2";
        Sys.Values["c"] = "3";
        RollbackJournal journal = _h.NewJournal();
        foreach (string t in new[] { "a", "b", "c" })
        {
            await journal.ApplyAsync("fake", t, "changed", _h.Ct);
        }

        Sys.RestoreFault = t => t == "b" ? new InvalidOperationException("b is locked") : null;
        RollbackReport report = await journal.RollbackAllAsync(_h.Ct);

        Assert.Equal(2, report.Restored);
        Assert.False(report.Complete);
        Assert.Equal(RollbackMode.JournalReplay, report.Mode);
        RollbackFailure failure = Assert.Single(report.Failures);
        Assert.Equal("b", failure.Entry!.Target);
        Assert.Equal("b is locked", failure.Message);
        Assert.Equal([("a", "1"), ("b", "changed"), ("c", "3")], Sys.Values.Select(p => (p.Key, p.Value)));
        Assert.True(_h.Fs.File.Exists(_h.JournalPath));
        Assert.Equal(
            [JournalEntryState.RolledBack, JournalEntryState.Applied, JournalEntryState.RolledBack],
            (await journal.GetEntriesAsync(_h.Ct)).Select(e => e.State));
        Assert.True(await journal.HasPendingChangesAsync(_h.Ct));

        Sys.RestoreFault = null;
        RollbackReport retry = await _h.NewJournal().RollbackAllAsync(_h.Ct);

        Assert.Equal(1, retry.Restored);
        Assert.True(retry.Complete);
        Assert.Equal([("a", "1"), ("b", "2"), ("c", "3")], Sys.Values.Select(p => (p.Key, p.Value)));
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
    }

    [Fact]
    public async Task Second_rollback_is_a_no_op()
    {
        RollbackJournal journal = _h.NewJournal();
        RollbackReport none = await journal.RollbackAllAsync(_h.Ct);
        Assert.Equal(0, none.Restored);
        Assert.Empty(none.Failures);
        Assert.True(none.Complete);
        Assert.Equal(RollbackMode.JournalReplay, none.Mode);

        await journal.ApplyAsync("fake", "a", "x", _h.Ct);
        await journal.RollbackAllAsync(_h.Ct);
        string[] logAfterFirst = [.. Sys.RestoreLog];

        RollbackReport second = await journal.RollbackAllAsync(_h.Ct);

        Assert.Equal(0, second.Restored);
        Assert.Empty(second.Failures);
        Assert.True(second.Complete);
        Assert.Equal(RollbackMode.JournalReplay, second.Mode);
        Assert.Equal(logAfterFirst, Sys.RestoreLog);
    }

    [Fact]
    public async Task Tampered_journal_falls_back_to_marker_sweep()
    {
        Sys.Values["foreign"] = "keep";
        Sys.Owners["foreign"] = "other-install";
        RollbackJournal journal = _h.NewJournal();
        await journal.ApplyAsync("fake", "a", "1", _h.Ct);
        await journal.ApplyAsync("fake", "b", "2", _h.Ct);
        byte[] bytes = _h.Fs.File.ReadAllBytes(_h.JournalPath);
        bytes[^1] ^= 0x01;
        _h.Fs.File.WriteAllBytes(_h.JournalPath, bytes);

        RollbackReport report = await _h.NewJournal().RollbackAllAsync(_h.Ct);

        Assert.Equal(RollbackMode.MarkerSweep, report.Mode);
        Assert.Equal(2, report.Restored);
        Assert.Empty(report.Failures);
        Assert.True(report.Complete);
        Assert.Equal([("foreign", "keep")], Sys.Values.Select(p => (p.Key, p.Value)));
        Assert.Empty(Sys.RestoreLog);
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
    }

    [Fact]
    public async Task Authentic_journal_with_invalid_json_also_sweeps()
    {
        await _h.NewJournal().ApplyAsync("fake", "a", "1", _h.Ct);
        await _h.Store.WriteAsync(_h.JournalPath, "{ not json"u8.ToArray(), _h.Ct);

        RollbackReport report = await _h.NewJournal().RollbackAllAsync(_h.Ct);

        Assert.Equal(RollbackMode.MarkerSweep, report.Mode);
        Assert.Equal(1, report.Restored);
        Assert.True(report.Complete);
        Assert.Empty(Sys.Values);
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
    }

    [Fact]
    public async Task Sweep_runs_on_every_handler_and_sums_the_counts()
    {
        var otherSystem = new FakeSystem();
        var other = new FakeHandler(otherSystem, JournalHarness.InstallId, "other");
        RollbackJournal journal = _h.NewJournal(other);
        await journal.ApplyAsync("fake", "a", "1", _h.Ct);
        await journal.ApplyAsync("other", "x", "1", _h.Ct);
        await journal.ApplyAsync("other", "y", "1", _h.Ct);
        _h.Fs.File.WriteAllBytes(_h.JournalPath, [1, 2, 3]);

        RollbackReport report = await _h.NewJournal(other).RollbackAllAsync(_h.Ct);

        Assert.Equal(RollbackMode.MarkerSweep, report.Mode);
        Assert.Equal(3, report.Restored);
        Assert.Empty(Sys.Values);
        Assert.Empty(otherSystem.Values);
    }

    [Fact]
    public async Task Failed_sweep_keeps_the_damaged_journal_and_a_retry_completes()
    {
        await _h.NewJournal().ApplyAsync("fake", "a", "1", _h.Ct);
        _h.Fs.File.WriteAllBytes(_h.JournalPath, [9, 9, 9]);
        Sys.SweepFault = () => new InvalidOperationException("sweep boom");

        RollbackReport report = await _h.NewJournal().RollbackAllAsync(_h.Ct);

        Assert.Equal(RollbackMode.MarkerSweep, report.Mode);
        Assert.False(report.Complete);
        Assert.Equal(0, report.Restored);
        RollbackFailure failure = Assert.Single(report.Failures);
        Assert.Null(failure.Entry);
        Assert.Equal("fake: sweep boom", failure.Message);
        Assert.True(_h.Fs.File.Exists(_h.JournalPath));
        Assert.True(await _h.NewJournal().HasPendingChangesAsync(_h.Ct));

        Sys.SweepFault = null;
        RollbackReport retry = await _h.NewJournal().RollbackAllAsync(_h.Ct);

        Assert.True(retry.Complete);
        Assert.Equal(1, retry.Restored);
        Assert.False(_h.Fs.File.Exists(_h.JournalPath));
    }

    [Fact]
    public async Task Applying_on_top_of_a_damaged_journal_is_refused()
    {
        _h.Fs.File.WriteAllBytes(_h.JournalPath, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _h.NewJournal().ApplyAsync("fake", "a", "1", _h.Ct));

        Assert.Empty(Sys.Values);
    }

    [Fact]
    public async Task Entry_for_an_unregistered_kind_is_reported_as_a_failure()
    {
        var otherSystem = new FakeSystem();
        await _h.NewJournal(new FakeHandler(otherSystem, JournalHarness.InstallId, "other")).ApplyAsync("other", "x", "1", _h.Ct);

        RollbackReport report = await _h.NewJournal().RollbackAllAsync(_h.Ct);

        Assert.False(report.Complete);
        Assert.Equal("No handler registered for kind 'other'.", Assert.Single(report.Failures).Message);
        Assert.True(_h.Fs.File.Exists(_h.JournalPath));
    }

    [Fact]
    public async Task Concurrent_applies_are_serialized_with_unique_sequences()
    {
        RollbackJournal journal = _h.NewJournal();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => journal.ApplyAsync("fake", $"t{i}", "v", _h.Ct).AsTask()));

        IReadOnlyList<JournalEntry> entries = await journal.GetEntriesAsync(_h.Ct);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), entries.Select(e => e.Sequence));
        Assert.All(entries, e => Assert.Equal(JournalEntryState.Applied, e.State));
        Assert.Equal(20, Sys.Values.Count);
    }

    [Fact]
    public void Duplicate_handler_kinds_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _h.NewJournal(new FakeHandler(Sys, JournalHarness.InstallId)));
    }

    [Fact]
    public async Task Journal_is_unreadable_without_the_sealing_key()
    {
        await _h.NewJournal().ApplyAsync("fake", "a", "1", _h.Ct);
        _h.Fs.File.Delete(_h.Fs.Path.Combine(_h.Fs.Path.GetDirectoryName(_h.JournalPath)!, "hmac.key"));
        var protector = new FakeSecretProtector();
        var freshStore = new SecureFileStore(_h.Fs, protector, new HmacKeyStore(_h.Fs, protector, _h.Fs.Path.Combine(_h.Fs.Path.GetDirectoryName(_h.JournalPath)!, "hmac.key")));

        await Assert.ThrowsAsync<TamperDetectedException>(async () => await freshStore.ReadAsync(_h.JournalPath, _h.Ct));
    }
}
