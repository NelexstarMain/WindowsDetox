using System.IO.Abstractions.TestingHelpers;
using Blocker.Contracts;
using Blocker.Core.AppRules;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Sdk.Events;
using static Blocker.Core.Tests.AppRules.AppRuleEnforcerHarness;
using static Blocker.Core.Tests.AppRules.AppRuleTestData;

namespace Blocker.Core.Tests.AppRules;

public sealed class AppRuleEnforcerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileInterval = AppRuleEnforcer.ReconcileInterval;

    private static ProcessInfo P(int pid, string path, int session = 1) => Orchestration.OrchestrationHarness.P(pid, path, session: session);

    // ---- blocking a process that starts ----

    [Fact]
    public async Task A_process_that_starts_inside_a_scheduled_block_is_terminated_and_replaced_by_the_block_screen()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, GamePath));

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        (string path, string arguments) = Assert.Single(h.Launcher.Launches);
        Assert.Equal(Orchestration.OrchestrationHarness.StubPath, path);
        Assert.Equal($"--blocked \"{GamePath}\" --reason AppScheduledBlock", arguments);
        ProcessBlocked blocked = Assert.Single(h.BlockedEvents);
        Assert.Equal(GamePath, blocked.Process.ExecutablePath);
        Assert.Equal(20, blocked.Process.ProcessId);
    }

    [Fact]
    public async Task A_process_that_starts_with_an_exhausted_pool_is_blocked_for_that_reason()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(0)));
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, GamePath));

        Assert.Single(h.Controller.Terminated);
        Assert.EndsWith("--reason AppBudgetExhausted", Assert.Single(h.Launcher.Launches).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowed_apps_and_apps_without_a_rule_are_left_alone()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(60), Range(0, 11 * 60, 12 * 60)), RuleFor(new AllowlistEntryDto("Chat", ChatPath, null, null), Daily(0)));
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, GamePath)); // 10:00: no block, pool unused
        await h.SpawnAsync(P(21, @"C:\Apps\unrelated.exe"));
        await h.TickAfterAsync(ReconcileInterval);
        Assert.Empty(h.Controller.Terminated);

        await h.SpawnAsync(P(22, ChatPath)); // the app with the empty pool is the only one that dies

        Assert.Equal([(22, 22_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task A_rule_set_without_anything_that_can_block_never_starts_the_monitor()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule());
        await h.Enforcer.StartAsync(Ct);
        await h.TickAfterAsync(ReconcileInterval);

        Assert.Equal(0, h.Monitor.StartCalls);
        Assert.Equal(0, h.Monitor.Handlers);
    }

    [Fact]
    public async Task The_monitor_starts_lazily_when_the_first_rule_that_can_block_is_saved()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.Enforcer.StartAsync(Ct);
        Assert.Equal(0, h.Monitor.StartCalls);

        await h.SetRulesAsync(AllWeekBlock());
        await h.TickAfterAsync(Second);
        await h.SpawnAsync(P(20, GamePath));

        Assert.Equal(1, h.Monitor.StartCalls);
        Assert.Single(h.Controller.Terminated);
    }

    [Fact]
    public async Task A_hash_rule_catches_the_binary_wherever_it_lives_and_ignores_unreadable_or_different_files()
    {
        await using var h = new AppRuleEnforcerHarness();
        string hash = Orchestration.OrchestrationHarness.Sha(HashedBytes);
        h.Fs.AddFile(Hashed, new MockFileData(HashedBytes));
        h.Fs.AddFile(@"C:\Other\different.exe", new MockFileData("other"u8.ToArray()));
        await h.SetRulesAsync(RuleFor(new AllowlistEntryDto("Hashed", null, hash, null), Daily(0)));
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, Hashed));
        await h.SpawnAsync(P(21, @"C:\Other\different.exe"));
        await h.SpawnAsync(P(22, @"C:\Other\missing.exe"));

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task A_package_rule_catches_every_process_of_the_package()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(RuleFor(new AllowlistEntryDto("Store app", null, null, "Pkg.Family_abc"), Daily(0)));
        h.Controller.Packages[20] = "pkg.family_ABC";
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, @"C:\Program Files\WindowsApps\Pkg\app.exe"));
        await h.SpawnAsync(P(21, @"C:\Program Files\WindowsApps\Other\app.exe"));

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task A_process_the_monitor_could_not_name_is_resolved_by_pid_and_skipped_when_it_stays_unnamed()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        h.Controller.Images[20] = GamePath;
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, string.Empty));
        await h.SpawnAsync(P(21, string.Empty));

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task One_process_is_terminated_once_even_while_it_lingers_in_the_snapshot_and_the_block_screen_is_not_repeated_within_its_window()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(20, GamePath)); // stays in the fake's snapshot after the kill
        await h.TickAfterAsync(ReconcileInterval);
        await h.TickAfterAsync(ReconcileInterval);
        await h.SpawnAsync(P(21, GamePath)); // a new instance right after the 10 s above: its screen is due again
        await h.SpawnAsync(P(22, GamePath));

        Assert.Equal([(20, 20_000L), (21, 21_000L), (22, 22_000L)], h.Controller.Terminated);
        Assert.Equal(2, h.Launcher.Launches.Count);
    }

    // ---- reconcile ----

    [Fact]
    public async Task A_running_instance_is_terminated_within_one_interval_when_its_scheduled_block_begins()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(null, Range(0, 11 * 60, 12 * 60)));
        h.Monitor.Running.Add(P(20, GamePath));
        await h.Enforcer.StartAsync(Ct);

        await h.TickAfterAsync(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(57));
        Assert.Empty(h.Controller.Terminated); // 10:59:57

        await h.TickAfterAsync(ReconcileInterval); // 11:00:02

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.EndsWith("AppScheduledBlock", Assert.Single(h.Launcher.Launches).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_instance_is_terminated_the_moment_the_foreground_time_uses_up_the_pool()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(1)));
        h.Monitor.Running.Add(P(20, GamePath));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);

        for (int i = 0; i < 59; i++)
        {
            await h.TickAfterAsync(Second);
        }

        Assert.Empty(h.Controller.Terminated);
        Assert.Equal(59, h.UsedSeconds());

        await h.TickAfterAsync(Second);

        Assert.Equal(60, h.UsedSeconds());
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated); // no waiting for the next reconcile
        Assert.EndsWith("AppBudgetExhausted", Assert.Single(h.Launcher.Launches).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_the_monitor_never_reported_is_found_by_the_reconcile_pass()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);

        h.Monitor.SpawnUnreported(P(20, GamePath));
        await h.TickAfterAsync(Second);
        Assert.Empty(h.Controller.Terminated); // the interval has not elapsed

        await h.TickAfterAsync(ReconcileInterval);

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task A_monitor_that_stopped_is_restarted_and_what_it_missed_is_enforced()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);
        Assert.Equal(1, h.Monitor.StartCalls);

        h.Monitor.Stop(new InvalidOperationException("session lost"));
        h.Monitor.SpawnUnreported(P(20, GamePath));
        await h.TickAfterAsync(ReconcileInterval);

        Assert.Equal(2, h.Monitor.StartCalls);
        Assert.Single(h.Controller.Terminated);
    }

    [Fact]
    public async Task A_monitor_that_cannot_start_does_not_stop_the_polling()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        h.Monitor.StartFault = new InvalidOperationException("no ETW");
        await h.Enforcer.StartAsync(Ct);

        h.Monitor.SpawnUnreported(P(20, GamePath));
        await h.TickAfterAsync(ReconcileInterval);

        Assert.Single(h.Controller.Terminated);
    }

    [Fact]
    public async Task A_failing_snapshot_is_survived()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);
        h.Monitor.EnumerateFault = new InvalidOperationException("snapshot failed");

        await h.TickAfterAsync(ReconcileInterval);
        h.Monitor.EnumerateFault = null;
        h.Monitor.SpawnUnreported(P(20, GamePath));
        await h.TickAfterAsync(ReconcileInterval);

        Assert.Single(h.Controller.Terminated);
    }

    // ---- sessions ----

    [Fact]
    public async Task Nothing_is_enforced_while_a_session_runs_and_enforcement_resumes_when_it_ends()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);
        h.SessionRunning = true;

        await h.SpawnAsync(P(20, GamePath));
        h.Monitor.SpawnUnreported(P(21, GamePath));
        await h.TickAfterAsync(ReconcileInterval);
        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);

        h.SessionRunning = false;
        await h.TickAfterAsync(ReconcileInterval);

        Assert.Equal([(20, 20_000L), (21, 21_000L)], h.Controller.Terminated.Order());
    }

    [Fact]
    public async Task Usage_counts_during_a_session_but_never_blocks_during_it()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(1)));
        h.Monitor.Running.Add(P(20, GamePath));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);
        h.SessionRunning = true;

        for (int i = 0; i < 70; i++)
        {
            await h.TickAfterAsync(Second);
        }

        Assert.Equal(70, h.UsedSeconds());
        Assert.Empty(h.Controller.Terminated);
        h.SessionRunning = false;
        await h.TickAfterAsync(ReconcileInterval);
        Assert.Single(h.Controller.Terminated); // the pool ran out meanwhile
    }

    // ---- guards ----

    [Fact]
    public async Task Critical_blocker_and_session_0_processes_are_never_terminated_even_with_a_rule()
    {
        await using var h = new AppRuleEnforcerHarness();
        var explorer = new AllowlistEntryDto("Explorer", Orchestration.OrchestrationHarness.Explorer, null, null);
        var ownUi = new AllowlistEntryDto("Blocker UI", @"C:\Program Files\Blocker\Blocker.UI.exe", null, null);
        await h.SetRulesAsync(AllWeekBlock(explorer), AllWeekBlock(ownUi), AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(10, Orchestration.OrchestrationHarness.Explorer));
        await h.SpawnAsync(P(11, @"C:\Program Files\Blocker\Blocker.UI.exe"));
        await h.SpawnAsync(P(12, GamePath, session: 0));
        h.Monitor.SpawnUnreported(P(13, Orchestration.OrchestrationHarness.Explorer));
        h.Monitor.SpawnUnreported(P(14, @"C:\Program Files\Blocker\Blocker.Service.exe"));
        await h.TickAfterAsync(ReconcileInterval);

        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);
    }

    [Fact]
    public async Task A_copy_of_a_critical_name_outside_the_system_directory_is_not_protected_by_its_name()
    {
        await using var h = new AppRuleEnforcerHarness();
        var copy = new AllowlistEntryDto("Fake explorer", @"C:\Users\me\Downloads\explorer.exe", null, null);
        await h.SetRulesAsync(AllWeekBlock(copy));
        await h.Enforcer.StartAsync(Ct);

        await h.SpawnAsync(P(10, @"C:\Users\me\Downloads\explorer.exe"));

        Assert.Single(h.Controller.Terminated);
    }

    [Fact]
    public async Task A_failing_terminate_does_not_stop_the_pipeline()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(AllWeekBlock());
        await h.Enforcer.StartAsync(Ct);
        h.Controller.TerminateFault = new InvalidOperationException("denied");

        await h.SpawnAsync(P(20, GamePath));
        h.Controller.TerminateFault = null;
        await h.SpawnAsync(P(21, GamePath));

        Assert.Equal([(21, 21_000L)], h.Controller.Terminated);
    }

    // ---- metering ----

    [Fact]
    public async Task Foreground_time_is_counted_per_second_for_the_app_in_front()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(60)), RuleFor(new AllowlistEntryDto("Chat", ChatPath, null, null), Daily(60)));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);

        for (int i = 0; i < 10; i++)
        {
            await h.TickAfterAsync(Second);
        }

        await h.Enforcer.OnForegroundAsync(ChatPath, null, Ct);
        for (int i = 0; i < 4; i++)
        {
            await h.TickAfterAsync(Second);
        }

        await h.Enforcer.OnForegroundAsync(@"C:\Apps\unrelated.exe", null, Ct);
        await h.TickAfterAsync(Second);

        Assert.Equal((10L, 4L), (h.UsedSeconds(0), h.UsedSeconds(1)));
    }

    [Fact]
    public async Task Metering_pauses_while_the_user_is_idle_and_resumes_afterwards()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(60)));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);

        h.Enforcer.SetIdle(true);
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);
        Assert.Equal(2, h.UsedSeconds());

        h.Enforcer.SetIdle(false);
        await h.TickAfterAsync(Second);

        Assert.Equal(3, h.UsedSeconds());
    }

    [Fact]
    public async Task A_foreground_change_ends_an_idle_state_nobody_confirmed_again()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(60)));
        await h.Enforcer.StartAsync(Ct);
        h.Enforcer.SetIdle(true); // a stale or forged report: the agent sends no heartbeat that would ever end it
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);

        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);

        Assert.Equal(2, h.UsedSeconds());
    }

    [Fact]
    public async Task Metering_pauses_without_an_agent_and_needs_a_fresh_foreground_report_after_it_returns()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(60)));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);
        await h.TickAfterAsync(Second);

        h.AgentConnected = false;
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);
        h.AgentConnected = true;
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);
        Assert.Equal(1, h.UsedSeconds()); // nobody said what is in front after the agent came back

        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);
        await h.TickAfterAsync(Second);

        Assert.Equal(2, h.UsedSeconds());
    }

    [Fact]
    public async Task One_stretch_of_metering_never_counts_more_than_five_seconds()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(600)));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);

        await h.TickAfterAsync(TimeSpan.FromMinutes(30)); // the process was suspended, the clock jumped

        Assert.Equal(5, h.UsedSeconds());
    }

    [Fact]
    public async Task A_foreground_report_is_matched_by_hash_and_package_like_a_process_is()
    {
        await using var h = new AppRuleEnforcerHarness();
        string hash = Orchestration.OrchestrationHarness.Sha(HashedBytes);
        h.Fs.AddFile(Hashed, new MockFileData(HashedBytes));
        await h.SetRulesAsync(
            RuleFor(new AllowlistEntryDto("Hashed", null, hash, null), Daily(60)),
            RuleFor(new AllowlistEntryDto("Store app", null, null, "Pkg.Family_abc"), Daily(60)));
        await h.Enforcer.StartAsync(Ct);

        await h.Enforcer.OnForegroundAsync(Hashed, null, Ct);
        await h.TickAfterAsync(Second);
        await h.Enforcer.OnForegroundAsync(@"C:\Program Files\WindowsApps\Pkg\app.exe", "PKG.family_abc", Ct);
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);

        Assert.Equal((1L, 2L), (h.UsedSeconds(0), h.UsedSeconds(1)));
    }

    [Fact]
    public async Task Rules_saved_while_an_app_is_in_front_apply_to_it_without_a_new_foreground_report()
    {
        await using var h = new AppRuleEnforcerHarness();
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);
        await h.TickAfterAsync(Second);

        await h.SetRulesAsync(Rule(Daily(60)));
        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);

        Assert.Equal(2, h.UsedSeconds());
    }

    [Fact]
    public async Task Usage_is_written_every_thirty_seconds_and_when_the_enforcer_is_disposed()
    {
        var h = new AppRuleEnforcerHarness();
        await h.SetRulesAsync(Rule(Daily(60)));
        await h.Enforcer.StartAsync(Ct);
        await h.Enforcer.OnForegroundAsync(GamePath, null, Ct);
        for (int i = 0; i < 29; i++)
        {
            await h.TickAfterAsync(Second);
        }

        Assert.False(h.Fs.File.Exists(h.UsagePath));

        await h.TickAfterAsync(Second);
        Assert.True(h.Fs.File.Exists(h.UsagePath));

        await h.TickAfterAsync(Second);
        await h.TickAfterAsync(Second);
        await h.Enforcer.DisposeAsync();

        AppUsageState saved = await h.LoadUsageAsync();
        Assert.Equal(32, new AppUsageLedger(saved).DaySeconds(AppRuleIdentity.LedgerKey(Game), h.Time.GetUtcNow().UtcDateTime));
    }
}
