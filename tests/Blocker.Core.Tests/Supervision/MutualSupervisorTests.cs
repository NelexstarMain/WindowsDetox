using System.Collections.Concurrent;
using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Tests.PluginRuntime;
using Blocker.Core.Security;
using Blocker.Core.Supervision;
using Blocker.Core.Tests.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.Supervision;

/// <summary>A partner service that starts when told to and "dies" when the test says so.</summary>
internal sealed class FakePartner : IServiceControl, IProcessWaiter
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _exits = new();
    private ServiceRunState _state;
    private int _pid;
    private int _starts;

    public FakePartner(ServiceRunState initial = ServiceRunState.Stopped) => _state = initial;

    public int Starts => Volatile.Read(ref _starts);

    /// <summary>Gets or sets whether <see cref="Start"/> throws.</summary>
    public bool FailStarts { get; set; }

    /// <summary>Gets or sets whether the partner dies the moment it is waited on (a crash loop).</summary>
    public bool DieImmediately { get; set; }

    public ServiceRunState QueryStatus(string serviceName)
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void Start(string serviceName)
    {
        lock (_gate)
        {
            _starts++;
            if (FailStarts)
            {
                throw new InvalidOperationException("cannot start");
            }

            _state = ServiceRunState.Running;
            _pid++;
        }
    }

    public int? GetProcessId(string serviceName)
    {
        lock (_gate)
        {
            return _state == ServiceRunState.Running ? _pid : null;
        }
    }

    public Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        if (DieImmediately)
        {
            Die();
            return Task.CompletedTask;
        }

        return _exits.GetOrAdd(processId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
    }

    /// <summary>Kills the running partner: the state becomes stopped and waiters complete.</summary>
    public void Die()
    {
        int pid;
        lock (_gate)
        {
            _state = ServiceRunState.Stopped;
            pid = _pid;
        }

        _exits.GetOrAdd(pid, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}

public sealed class MutualSupervisorTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new();
    private readonly FakePartner _partner = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _supervise = true;

    private Task Run(MutualSupervisorOptions? options = null)
    {
        var supervisor = new MutualSupervisor(
            "Partner",
            _partner,
            _partner,
            _ => ValueTask.FromResult(Volatile.Read(ref _supervise)),
            _time,
            NullLogger<MutualSupervisor>.Instance,
            options);
        return Task.Run(() => supervisor.RunAsync(_stop.Token), Ct);
    }

    private async Task StopAsync(Task run)
    {
        await _stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_stopped_partner_is_started_at_once_and_restarted_the_moment_it_exits()
    {
        DateTimeOffset start = _time.GetUtcNow();
        Task run = Run();

        await Spin.UntilAsync(() => _partner.Starts == 1);
        _partner.Die();
        await Spin.UntilAsync(() => _partner.Starts == 2);
        _partner.Die();
        await Spin.UntilAsync(() => _partner.Starts == 3);

        Assert.Equal(start, _time.GetUtcNow());
        await StopAsync(run);
    }

    [Fact]
    public async Task A_running_partner_is_left_alone()
    {
        var partner = new FakePartner();
        partner.Start("Partner");
        var supervisor = new MutualSupervisor("Partner", partner, partner, _ => ValueTask.FromResult(true), _time, NullLogger<MutualSupervisor>.Instance);
        Task run = Task.Run(() => supervisor.RunAsync(_stop.Token), Ct);

        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(1, partner.Starts);
        await StopAsync(run);
    }

    [Fact]
    public async Task Nothing_is_restarted_while_supervision_is_off_and_supervision_resumes_when_it_turns_on()
    {
        _supervise = false;
        Task run = Run();

        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        Assert.Equal(0, _partner.Starts);

        Volatile.Write(ref _supervise, true);
        await Spin.UntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            return _partner.Starts == 1;
        });
        await StopAsync(run);
    }

    [Fact]
    public async Task A_partner_that_exits_after_supervision_ended_is_not_restarted()
    {
        Task run = Run();
        await Spin.UntilAsync(() => _partner.Starts == 1);

        Volatile.Write(ref _supervise, false);
        _partner.Die();
        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        Assert.Equal(1, _partner.Starts);
        await StopAsync(run);
    }

    [Fact]
    public async Task Starts_back_off_exponentially_only_after_ten_failures_within_a_minute()
    {
        _partner.FailStarts = true;
        Task run = Run();

        await Spin.UntilAsync(() => _partner.Starts == 10);
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(10, _partner.Starts);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(10, _partner.Starts);
        await Spin.UntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            return _partner.Starts == 11;
        });

        // The next delay is two seconds.
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        _time.Advance(TimeSpan.FromMilliseconds(1999));
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(11, _partner.Starts);
        await Spin.UntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            return _partner.Starts == 12;
        });
        await StopAsync(run);
    }

    [Fact]
    public async Task A_partner_that_dies_right_after_every_start_counts_as_failing()
    {
        _partner.DieImmediately = true;
        Task run = Run();

        await Spin.UntilAsync(() => _partner.Starts == 10);
        for (int i = 0; i < 50; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(10, _partner.Starts);
        await Spin.UntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));

            // A starved supervisor can wake only after the failure window has passed and restart several times
            // between two checks, so "at least one more start" is the observable property.
            return _partner.Starts >= 11;
        });
        await StopAsync(run);
    }

    [Fact]
    public async Task A_partner_that_ran_for_a_while_before_dying_never_triggers_back_off()
    {
        Task run = Run();

        for (int round = 1; round <= 14; round++)
        {
            await Spin.UntilAsync(() => _partner.Starts == round);
            _time.Advance(TimeSpan.FromSeconds(6));
            _partner.Die();
        }

        await Spin.UntilAsync(() => _partner.Starts == 15);
        await StopAsync(run);
    }

    [Fact]
    public async Task A_partner_in_a_transitional_state_is_polled_not_started()
    {
        var partner = new FakePartner(ServiceRunState.StartPending);
        var supervisor = new MutualSupervisor("Partner", partner, partner, _ => ValueTask.FromResult(true), _time, NullLogger<MutualSupervisor>.Instance);
        Task run = Task.Run(() => supervisor.RunAsync(_stop.Token), Ct);

        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        Assert.Equal(0, partner.Starts);
        await StopAsync(run);
    }

    // ---- status file ----

    [Fact]
    public async Task The_status_file_round_trips_and_a_missing_file_means_no_supervision()
    {
        var fs = new MockFileSystem();
        SupervisionStatusFile file = StatusFile(fs);

        Assert.False(await file.ShouldSuperviseAsync(Ct));

        await file.WriteAsync(new SupervisionStatus(true, Guid.NewGuid(), _time.GetUtcNow()), Ct);
        Assert.True(await file.ShouldSuperviseAsync(Ct));

        await file.WriteAsync(new SupervisionStatus(false, null, _time.GetUtcNow()), Ct);
        Assert.False(await file.ShouldSuperviseAsync(Ct));
    }

    [Fact]
    public async Task A_forged_or_damaged_status_file_means_supervision()
    {
        var fs = new MockFileSystem();
        SupervisionStatusFile file = StatusFile(fs);
        await file.WriteAsync(new SupervisionStatus(false, null, _time.GetUtcNow()), Ct);

        byte[] sealedBytes = fs.File.ReadAllBytes("/pd/status.bin");
        sealedBytes[^1] ^= 0xFF;
        fs.File.WriteAllBytes("/pd/status.bin", sealedBytes);
        Assert.True(await file.ShouldSuperviseAsync(Ct));

        fs.File.WriteAllText("/pd/status.bin", "{\"sessionActive\":false}");
        Assert.True(await file.ShouldSuperviseAsync(Ct));
    }

    private static SupervisionStatusFile StatusFile(MockFileSystem fs)
    {
        var protector = new FakeSecretProtector();
        var store = new SecureFileStore(fs, protector, new HmacKeyStore(fs, protector, "/pd/hmac.key"));
        return new SupervisionStatusFile(store, "/pd/status.bin", NullLogger<SupervisionStatusFile>.Instance);
    }
}
