using Blocker.Contracts;
using Blocker.Core.Session;

namespace Blocker.Core.AppRules;

/// <summary>A rule that blocks its app at this moment.</summary>
/// <param name="Key">Ledger key of the rule.</param>
/// <param name="Rule">The rule.</param>
/// <param name="State"><see cref="AppRuleState.ScheduledBlock"/> or <see cref="AppRuleState.BudgetExhausted"/>.</param>
internal sealed record BlockingRule(string Key, AppRuleDto Rule, AppRuleState State);

/// <summary>
/// The rules in force, their frozen time zone and the usage ledger, with every decision made on top of them. Rules are replaced as a whole and
/// read lock-free as one snapshot; the ledger is guarded by a lock. All wall-clock reasoning uses the zone captured when the rules were saved,
/// so changing the Windows time zone afterwards does not move a schedule or a day boundary. A session is never consulted when rules are
/// replaced: the check whether an app is blocked looks through a running session, so a session cannot be used to loosen a rule.
/// </summary>
/// <param name="rulesStore">Stored rules.</param>
/// <param name="usageStore">Stored usage.</param>
/// <param name="utcNow">Verified (or system) UTC.</param>
/// <param name="currentZoneId">Zone id of the machine, read when rules are saved.</param>
internal sealed class AppRuleEngine(AppRulesStore rulesStore, AppUsageStore usageStore, Func<DateTimeOffset> utcNow, Func<string> currentZoneId)
{
    private readonly Lock _ledgerGate = new();
    private readonly SemaphoreSlim _setGate = new(1, 1);
    private AppUsageLedger _ledger = new();
    private bool _usageDirty;
    private Snapshot _snapshot = Snapshot.Create(new AppRulesDto([]), TimeZoneInfo.Utc);

    /// <summary>Raised after the rules were replaced.</summary>
    public event EventHandler? RulesChanged;

    /// <summary>Gets the rules in force.</summary>
    public AppRulesDto Rules => Volatile.Read(ref _snapshot).Rules;

    /// <summary>Gets the id of the zone the rules are read in.</summary>
    public string ZoneId => Volatile.Read(ref _snapshot).Zone.Id;

    /// <summary>Gets whether any rule can ever block (a scheduled range or a pool).</summary>
    public bool CanBlock => Volatile.Read(ref _snapshot).CanBlock;

    /// <summary>Gets a matcher over every rule.</summary>
    public AppRuleMatcher Matcher => Volatile.Read(ref _snapshot).Matcher;

    /// <summary>Loads the stored rules and usage. Call once before the engine is used.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when everything is loaded.</returns>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        StoredAppRules stored = await rulesStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        AppRulesDto rules = AppRuleValidator.Validate(stored.Rules).Rules ?? new AppRulesDto([]);
        TimeZoneInfo zone = SessionConfigValidator.TryResolveZone(stored.TimeZoneId) ?? SessionConfigValidator.TryResolveZone(currentZoneId()) ?? TimeZoneInfo.Utc;
        Volatile.Write(ref _snapshot, Snapshot.Create(rules, zone));
        AppUsageState usage = await usageStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_ledgerGate)
        {
            _ledger = new AppUsageLedger(usage);
        }
    }

    /// <summary>Gets the local wall-clock time in the frozen zone.</summary>
    public DateTime LocalNow() => ToLocal(utcNow(), Volatile.Read(ref _snapshot).Zone);

    /// <summary>
    /// Validates and replaces the rules. While an app is blocked (a scheduled block, or its pool used up) its new rule must be identical or
    /// stricter, and it cannot be removed; apps that are not blocked can be changed freely. The zone is captured from the machine unless an
    /// app is blocked, in which case the frozen zone stays.
    /// </summary>
    /// <param name="request">The rules as sent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Errors; empty when the rules were stored.</returns>
    public async ValueTask<IReadOnlyList<string>> SetAsync(AppRulesDto request, CancellationToken cancellationToken)
    {
        AppRulesValidation validation = AppRuleValidator.Validate(request);
        if (validation.Rules is not { } next)
        {
            return validation.Errors;
        }

        await _setGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot current = Volatile.Read(ref _snapshot);
            (List<string> refused, bool anyBlocked, List<(string From, string To)> moves) = CompareWithCurrent(current, next);
            if (refused.Count > 0)
            {
                return [.. refused.Take(AppRuleValidator.MaxErrors)];
            }

            TimeZoneInfo zone = anyBlocked ? current.Zone : SessionConfigValidator.TryResolveZone(currentZoneId()) ?? current.Zone;
            await rulesStore.SaveAsync(new StoredAppRules(next, zone.Id), cancellationToken).ConfigureAwait(false);
            lock (_ledgerGate)
            {
                foreach ((string from, string to) in moves)
                {
                    _ledger.Move(from, to);
                }

                _usageDirty |= moves.Count > 0;
                Volatile.Write(ref _snapshot, Snapshot.Create(next, zone));
            }
        }
        finally
        {
            _ = _setGate.Release();
        }

        RulesChanged?.Invoke(this, EventArgs.Empty);
        return [];
    }

    /// <summary>Returns the live state of every rule.</summary>
    /// <param name="sessionRunning">Whether a session (including an emergency pass) runs.</param>
    /// <returns>One entry per rule, in the order of the rules.</returns>
    public AppRulesStatusResponse GetStatus(bool sessionRunning)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        DateTime local = ToLocal(utcNow(), snapshot.Zone);
        List<AppRuleStatusDto> items = [];
        lock (_ledgerGate)
        {
            foreach (AppRuleDto rule in snapshot.Rules.Rules)
            {
                long used = _ledger.Used(AppRuleIdentity.LedgerKey(rule.App), rule.Budget.Mode, local);
                AppRuleEvaluation result = AppRuleEvaluator.Evaluate(rule, local, used, sessionRunning);
                DateTimeOffset? until = result.BlockedUntil is { } end ? new DateTimeOffset(end, snapshot.Zone.GetUtcOffset(end)) : null;
                items.Add(new AppRuleStatusDto(rule.App, result.State, used, result.RemainingSeconds, until));
            }
        }

        return new AppRulesStatusResponse(items);
    }

    /// <summary>Returns the rules that block their app right now, whether or not a session runs (the caller decides what a session means).</summary>
    /// <returns>The blocking rules, in rule order.</returns>
    public IReadOnlyList<BlockingRule> CurrentBlocks()
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        if (!snapshot.CanBlock)
        {
            return [];
        }

        DateTime local = ToLocal(utcNow(), snapshot.Zone);
        lock (_ledgerGate)
        {
            return [.. BlocksOf(snapshot, local)];
        }
    }

    /// <summary>Counts foreground time for the apps with these keys.</summary>
    /// <param name="keys">Ledger keys.</param>
    /// <param name="seconds">Seconds to add.</param>
    /// <returns><see langword="true"/> when this call used up the pool of at least one rule, so enforcement should act now.</returns>
    public bool AddUsage(IReadOnlyList<string> keys, long seconds)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        DateTime local = ToLocal(utcNow(), snapshot.Zone);
        bool exhausted = false;
        lock (_ledgerGate)
        {
            foreach (string key in keys)
            {
                AppRuleDto? rule = snapshot.Rules.Rules.FirstOrDefault(r => AppRuleIdentity.LedgerKey(r.App) == key);
                bool wasBlocked = rule is not null && IsBlocking(Evaluate(rule, key, local));
                _ledger.Add(key, seconds, local);
                exhausted |= rule is not null && !wasBlocked && IsBlocking(Evaluate(rule, key, local));
            }

            _usageDirty |= seconds > 0 && keys.Count > 0;
        }

        return exhausted;
    }

    /// <summary>Writes the usage to disk when it changed since the last write; stale entries are dropped first.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the state is durable (or nothing changed).</returns>
    public async ValueTask PersistUsageAsync(CancellationToken cancellationToken)
    {
        AppUsageState state;
        lock (_ledgerGate)
        {
            if (!_usageDirty)
            {
                return;
            }

            _ledger.Prune(ToLocal(utcNow(), Volatile.Read(ref _snapshot).Zone));
            state = _ledger.State;
            _usageDirty = false;
        }

        try
        {
            await usageStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_ledgerGate)
            {
                _usageDirty = true; // written again by the next attempt
            }

            throw;
        }
    }

    private static bool IsBlocking(AppRuleEvaluation result) => result.State is AppRuleState.ScheduledBlock or AppRuleState.BudgetExhausted;

    private static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    private AppRuleEvaluation Evaluate(AppRuleDto rule, string key, DateTime local) =>
        AppRuleEvaluator.Evaluate(rule, local, _ledger.Used(key, rule.Budget.Mode, local), sessionRunning: false);

    private IEnumerable<BlockingRule> BlocksOf(Snapshot snapshot, DateTime local)
    {
        foreach (AppRuleDto rule in snapshot.Rules.Rules)
        {
            string key = AppRuleIdentity.LedgerKey(rule.App);
            AppRuleEvaluation result = Evaluate(rule, key, local);
            if (IsBlocking(result))
            {
                yield return new BlockingRule(key, rule, result.State);
            }
        }
    }

    /// <summary>Finds what the replacement would loosen, whether anything is blocked now and which ledger keys change with the identity.</summary>
    private (List<string> Refused, bool AnyBlocked, List<(string From, string To)> Moves) CompareWithCurrent(Snapshot current, AppRulesDto next)
    {
        List<string> refused = [];
        List<(string From, string To)> moves = [];
        bool anyBlocked = false;
        DateTime local = ToLocal(utcNow(), current.Zone);
        lock (_ledgerGate)
        {
            foreach (AppRuleDto old in current.Rules.Rules)
            {
                string oldKey = AppRuleIdentity.LedgerKey(old.App);
                List<AppRuleDto> replacements = [.. next.Rules.Where(r => AppRuleIdentity.Overlap(r.App, old.App))];
                moves.AddRange(replacements.Select(r => (From: oldKey, To: AppRuleIdentity.LedgerKey(r.App))).Where(static m => m.From != m.To).Take(1));
                if (!IsBlocking(Evaluate(old, oldKey, local)))
                {
                    continue;
                }

                anyBlocked = true;
                string name = AppRuleValidator.Label(old.App);
                if (replacements.Count == 0)
                {
                    refused.Add($"'{name}' is blocked right now, so its rule cannot be removed.");
                }
                else if (!replacements.All(r => AppRuleTightness.IsNotLooserThan(r, old)))
                {
                    refused.Add($"'{name}' is blocked right now, so its rule can only be made stricter, not looser.");
                }
            }
        }

        return (refused, anyBlocked, moves);
    }

    private sealed record Snapshot(AppRulesDto Rules, TimeZoneInfo Zone, AppRuleMatcher Matcher, bool CanBlock)
    {
        public static Snapshot Create(AppRulesDto rules, TimeZoneInfo zone) => new(
            rules,
            zone,
            new AppRuleMatcher(rules.Rules),
            rules.Rules.Any(static r => r.BlockedRanges.Count > 0 || r.Budget.Mode != AppBudgetMode.None));
    }
}
