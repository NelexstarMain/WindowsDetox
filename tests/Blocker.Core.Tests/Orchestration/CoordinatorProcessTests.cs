using Blocker.Core.Tests.TestSupport;
using Blocker.Core.Ports;
using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Policy;
using Blocker.Sdk.Events;
using Blocker.Sdk.Extensions;
using Blocker.Sdk.Model;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorProcessTests
{
    private const string Svchost = @"C:\Windows\System32\svchost.exe";

    private static string StubArgs(string image, string reason) => $"--blocked \"{image}\" --reason {reason}";

    [Fact]
    public async Task BlockedProcess_IsTerminated_ShowsTheStub_AndPublishesTheEvent()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(20, Game));

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.Equal([(StubPath, StubArgs(Game, "NotAllowlisted"))], h.Launcher.Launches);
        ProcessBlocked blocked = Assert.Single(h.BlockedEvents);
        Assert.Equal(
            new ProcessDescriptor(20, 1, "game.exe", Game, null, null, new DateTimeOffset(DateTime.FromFileTimeUtc(20_000), TimeSpan.Zero)),
            blocked.Process);
        Assert.Equal(BlockReason.NotAllowlisted, blocked.Reason);
        Assert.Null(blocked.BlockedByPlugin);
        Assert.Equal(Noon, blocked.OccurredAt);
    }

    [Fact]
    public async Task Allowed_Critical_Session0_Baseline_Hashed_AndInheritedProcesses_AreLeftAlone()
    {
        await using var h = new OrchestrationHarness();
        string hashed = MockUnixSupport.Path(@"C:\Hashed\tool.exe");
        h.Fs.AddFile(hashed, new MockFileData(HashedBytes));
        h.Signatures.Signed.Add(Notepad);
        await h.StartAsync();

        await h.SpawnAsync(P(10, Explorer));
        await h.SpawnAsync(P(11, Svchost, session: 0));
        await h.SpawnAsync(P(12, Editor));
        await h.SpawnAsync(P(13, @"C:\Temp\helper.exe", parent: 12));
        await h.SpawnAsync(P(14, @"C:\Temp\sub.exe", parent: 13));
        await h.SpawnAsync(P(15, StubPath));
        await h.SpawnAsync(P(16, Notepad));
        await h.SpawnAsync(P(17, hashed));

        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);
        Assert.Empty(h.BlockedEvents);
        Assert.Equal([Notepad], h.Signatures.Calls); // only needed for the baseline rule
    }

    [Fact]
    public async Task BaselineRule_NeedsAMicrosoftSignatureAndASystemPath()
    {
        await using var h = new OrchestrationHarness();
        h.Signatures.Signed.Add(@"C:\Tools\signed.exe");
        await h.StartAsync();

        await h.SpawnAsync(P(20, @"C:\Windows\System32\unsigned.exe"));
        await h.SpawnAsync(P(21, @"C:\Tools\signed.exe"));

        Assert.Equal([(20, 20_000L), (21, 21_000L)], h.Controller.Terminated);
        Assert.Equal([@"C:\Windows\System32\unsigned.exe"], h.Signatures.Calls);
    }

    [Fact]
    public async Task HashAllowlist_DecidesByContent_NotByName()
    {
        await using var h = new OrchestrationHarness();
        string good = MockUnixSupport.Path(@"C:\Hashed\good.exe");
        string tampered = MockUnixSupport.Path(@"C:\Hashed\tool.exe");
        h.Fs.AddFile(good, new MockFileData(HashedBytes));
        h.Fs.AddFile(tampered, new MockFileData("something else"u8.ToArray()));
        await h.StartAsync();

        await h.SpawnAsync(P(20, good));
        await h.SpawnAsync(P(21, tampered));

        Assert.Equal([(21, 21_000L)], h.Controller.Terminated);
        Assert.Equal(Sha("something else"u8.ToArray()), Assert.Single(h.BlockedEvents).Process.Sha256);
    }

    [Fact]
    public async Task ChildWhoseRealCreatorIsNotTheClaimedAllowedParent_IsBlocked()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(12, Editor)); // allowlisted
        await h.SpawnAsync(P(13, @"C:\Temp\launcher.exe")); // not allowlisted: runs just long enough to spawn a child
        await h.SpawnAsync(P(14, @"C:\Temp\game2.exe", parent: 12) with { CreatorProcessId = 13 });

        Assert.Equal([(13, 13_000L), (14, 14_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task ChildWhoseRealCreatorIsTheAllowedParent_StillInherits()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(12, Editor));
        await h.SpawnAsync(P(14, @"C:\Temp\helper.exe", parent: 12) with { CreatorProcessId = 12 });

        Assert.Empty(h.Controller.Terminated);
    }

    [Fact]
    public async Task BypassToolStartedByAnAllowedApp_IsBlocked()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(12, Editor));
        await h.SpawnAsync(P(13, Cmd, parent: 12));

        Assert.Equal([(13, 13_000L)], h.Controller.Terminated);
        Assert.Equal([(StubPath, StubArgs(Cmd, "BypassTool"))], h.Launcher.Launches);
        Assert.Equal(BlockReason.BypassTool, Assert.Single(h.BlockedEvents).Reason);
    }

    [Fact]
    public async Task RenamedBypassTool_IsCaughtByItsHash()
    {
        await using var h = new OrchestrationHarness();
        string renamed = MockUnixSupport.Path(@"C:\Tools\notes.exe");
        h.Fs.AddFile(renamed, new MockFileData(BypassBytes));
        await h.StartAsync();

        await h.SpawnAsync(P(20, renamed));

        Assert.Equal([(StubPath, StubArgs(renamed, "BypassTool"))], h.Launcher.Launches);
        Assert.Equal(BlockReason.BypassTool, Assert.Single(h.BlockedEvents).Reason);
    }

    [Fact]
    public async Task ChildOfABlockedProcess_DoesNotInherit()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(20, Game));
        await h.SpawnAsync(P(21, @"C:\Games\child.exe", parent: 20));

        Assert.Equal([(20, 20_000L), (21, 21_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task PackagedApp_IsAllowedByItsFamilyName_AndItsChildrenInherit()
    {
        await using var h = new OrchestrationHarness();
        h.Controller.Packages[30] = "Pkg_123";
        await h.StartAsync(allowlist: new Allowlist([new AllowlistEntry("Store app", null, null, "Pkg_123")]));

        await h.SpawnAsync(P(30, @"C:\Program Files\WindowsApps\Pkg\app.exe"));
        await h.SpawnAsync(P(31, @"C:\Program Files\WindowsApps\Pkg\renderer.exe", parent: 30));
        await h.SpawnAsync(P(32, @"C:\Program Files\WindowsApps\Other\app.exe"));

        Assert.Equal([(32, 32_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task ConsoleHostOfWindowsTerminal_IsLeftAlone_WhileTheTerminalItselfStaysBlocked()
    {
        // Windows hands the console of a starting console program (allowlisted or not) to OpenConsole.exe when Windows Terminal is the default
        // terminal; killing it during that start fails the program with STATUS_DLL_INIT_FAILED (0xC0000142).
        const string package = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.22.3232.0_x64__8wekyb3d8bbwe";
        await using var h = new OrchestrationHarness(settings: Settings with { WindowsAppsDirectory = @"C:\Program Files\WindowsApps" });
        await h.StartAsync();

        await h.SpawnAsync(P(40, package + @"\OpenConsole.exe"));
        await h.SpawnAsync(P(41, package + @"\WindowsTerminal.exe"));

        Assert.Equal([(41, 41_000L)], h.Controller.Terminated);
        Assert.Equal(BlockReason.BypassTool, Assert.Single(h.BlockedEvents).Reason);
    }

    [Fact]
    public async Task SignedImageDirectlyInTheWindowsDirectory_IsLeftAlone()
    {
        const string spooler = @"C:\Windows\splwow64.exe";
        await using var h = new OrchestrationHarness();
        h.Signatures.Signed.Add(spooler);
        await h.StartAsync();

        await h.SpawnAsync(P(20, spooler));

        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);
    }

    [Fact]
    public async Task StubLaunches_AreDeduplicatedPerImageForTwoMinutes()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(20, Game));
        await h.SpawnAsync(P(21, Game));
        await h.SpawnAsync(P(23, @"C:\Games\other.exe"));
        h.Time.Advance(TimeSpan.FromSeconds(119.9));
        await h.SpawnAsync(P(24, Game));
        Assert.Equal(
            [(StubPath, StubArgs(Game, "NotAllowlisted")), (StubPath, StubArgs(@"C:\Games\other.exe", "NotAllowlisted"))],
            h.Launcher.Launches);

        h.Time.Advance(TimeSpan.FromSeconds(0.1));
        await h.SpawnAsync(P(22, Game));

        Assert.Equal(3, h.Launcher.Launches.Count);
        Assert.Equal((StubPath, StubArgs(Game, "NotAllowlisted")), h.Launcher.Launches[2]);
        Assert.Equal([20, 21, 23, 24, 22], h.Controller.Terminated.Select(static t => t.ProcessId)); // every process is still terminated
        Assert.Equal(5, h.BlockedEvents.Count);
    }

    [Fact]
    public async Task StartSnapshot_EnforcesParentsFirst_AndSkipsWhatTheQueueRepeats()
    {
        await using var h = new OrchestrationHarness();
        h.Monitor.Running.AddRange([P(13, @"C:\Temp\helper.exe", parent: 12), P(20, Game), P(12, Editor), P(10, Explorer)]);
        h.Monitor.OnEnumerate = () => h.Monitor.Spawn(P(21, @"C:\Games\late.exe")); // started while the snapshot is taken: also queued

        await h.StartAsync();
        await h.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal([(20, 20_000L), (21, 21_000L)], h.Controller.Terminated.OrderBy(static t => t.ProcessId));
        Assert.Equal(2, h.BlockedEvents.Count);
        Assert.Equal(2, h.Launcher.Launches.Count);
    }

    [Fact]
    public async Task ExitEvents_AreProcessedInOrder()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        ProcessInfo editor = P(12, Editor);
        await h.SpawnAsync(editor);

        h.Monitor.Exit(editor);
        await h.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // A late child of the exited allowed parent is still recognised (the node is kept), a stranger is not.
        await h.SpawnAsync(P(13, @"C:\Temp\late-child.exe", parent: 12));
        await h.SpawnAsync(P(14, @"C:\Temp\stranger.exe", parent: 99));
        Assert.Equal([(14, 14_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task FailingPorts_NeverStopThePipeline()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        h.Controller.TerminateFault = new InvalidOperationException("access denied");
        h.Launcher.Fault = new InvalidOperationException("no session");
        await h.SpawnAsync(P(20, Game));
        Assert.Empty(h.Controller.Terminated);
        Assert.Single(h.BlockedEvents); // the event is published although terminate and launch failed

        h.Controller.TerminateFault = null;
        h.Launcher.Fault = null;
        h.Launcher.Result = null; // nobody logged on
        await h.SpawnAsync(P(21, @"C:\Games\next.exe"));

        Assert.Equal([(21, 21_000L)], h.Controller.Terminated);
        Assert.Equal(2, h.BlockedEvents.Count);
    }

    [Fact]
    public async Task Interceptor_CanBlockNonCriticalProcesses_ButNeverCriticalOnes()
    {
        List<int> consulted = [];
        await using var h = new OrchestrationHarness(interceptor: (d, _) =>
        {
            lock (consulted)
            {
                consulted.Add(d.ProcessId);
            }

            return ValueTask.FromResult<IReadOnlyList<InterceptDecision>>([InterceptDecision.Abstain, InterceptDecision.Block("Focus time")]);
        });
        await h.StartAsync();

        await h.SpawnAsync(P(10, Explorer));
        await h.SpawnAsync(P(11, StubPath));
        await h.SpawnAsync(P(12, Svchost, session: 0));
        await h.SpawnAsync(P(13, Editor));
        await h.SpawnAsync(P(14, Game));

        Assert.Equal([13], consulted); // protected processes are never offered; core-blocked ones need no opinion
        Assert.Equal([(13, 13_000L), (14, 14_000L)], h.Controller.Terminated);
        Assert.Equal(
            [(StubPath, StubArgs(Editor, "PluginInterceptor")), (StubPath, StubArgs(Game, "NotAllowlisted"))],
            h.Launcher.Launches);
        Assert.Equal([BlockReason.PluginPolicy, BlockReason.NotAllowlisted], h.BlockedEvents.Select(static e => e.Reason));
    }

    [Fact]
    public async Task Interceptor_AbstainingFailingOrSlow_LeavesTheCoreVerdict()
    {
        int calls = 0;
        await using var h = new OrchestrationHarness(interceptor: async (d, ct) =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    return [InterceptDecision.Abstain];
                case 2:
                    throw new InvalidOperationException("plugin crashed");
                default:
                    await Hang.UntilCancelled(ct); // hangs until the 100 ms budget cancels it
                    return [InterceptDecision.Block("never reached")];
            }
        });
        await h.StartAsync();

        await h.SpawnAsync(P(12, Editor));
        await h.SpawnAsync(P(13, Editor));
        h.Monitor.Spawn(P(14, Editor));
        await h.AdvanceUntilAsync(h.Coordinator.DrainAsync(), TimeSpan.FromMilliseconds(50));

        Assert.Equal(3, calls);
        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.BlockedEvents);
    }
}
