using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Journal;
using Blocker.Core.Security;
using Blocker.Core.Tests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocker.Core.Tests.Journal;

/// <summary>Thrown by fakes to simulate the machine losing power mid-operation.</summary>
public sealed class PowerLossException : Exception
{
    public PowerLossException() : base("power loss") { }
}

/// <summary>In-memory "system" the fake handlers change; every artifact we create is tagged with its owner (install id).</summary>
public sealed class FakeSystem
{
    public SortedDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Owners { get; } = [];

    public List<string> RestoreLog { get; } = [];

    public bool PowerLost { get; set; }

    public Action<string>? OnApply { get; set; }

    public Func<string, Exception?>? ApplyFault { get; set; }

    public Func<string, Exception?>? RestoreFault { get; set; }

    public Func<Exception?>? SweepFault { get; set; }
}

public sealed class FakeHandler(FakeSystem system, string installId, string kind = "fake") : IReversibleChangeHandler
{
    public string Kind => kind;

    public ValueTask<string?> CaptureOriginalAsync(string target, CancellationToken cancellationToken) =>
        ValueTask.FromResult(system.Values.TryGetValue(target, out string? value) ? value : null);

    public ValueTask ApplyAsync(string target, string desiredStateJson, CancellationToken cancellationToken)
    {
        system.OnApply?.Invoke(target);
        if (system.ApplyFault?.Invoke(target) is { } fault)
        {
            throw fault;
        }

        system.Values[target] = desiredStateJson;
        system.Owners[target] = installId;
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreAsync(string target, string? originalStateJson, CancellationToken cancellationToken)
    {
        if (system.RestoreFault?.Invoke(target) is { } fault)
        {
            throw fault;
        }

        system.RestoreLog.Add(target);
        system.Owners.Remove(target);
        if (originalStateJson is null)
        {
            system.Values.Remove(target);
        }
        else
        {
            system.Values[target] = originalStateJson;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> SweepAsync(string installId, CancellationToken cancellationToken)
    {
        if (system.SweepFault?.Invoke() is { } fault)
        {
            throw fault;
        }

        string[] owned = [.. system.Owners.Where(p => p.Value == installId).Select(p => p.Key)];
        foreach (string target in owned)
        {
            system.Values.Remove(target);
            system.Owners.Remove(target);
        }

        return ValueTask.FromResult(owned.Length);
    }
}

/// <summary>Wires a mock file system, fake protector, store and fake handler; <see cref="NewJournal"/> simulates a process restart.</summary>
public sealed class JournalHarness
{
    public const string InstallId = "install-1";

    public MockFileSystem Fs { get; } = new();

    public FakeSystem System { get; } = new();

    public SecureFileStore Store { get; }

    public string JournalPath { get; } = MockUnixSupport.Path(@"C:\ProgramData\Blocker\journal.bin");

    public CancellationToken Ct { get; } = TestContext.Current.CancellationToken;

    public JournalHarness()
    {
        Fs.Directory.CreateDirectory(Fs.Path.GetDirectoryName(JournalPath)!);
        var protector = new FakeSecretProtector();
        Store = new SecureFileStore(Fs, protector, new HmacKeyStore(Fs, protector, MockUnixSupport.Path(@"C:\ProgramData\Blocker\hmac.key")));
    }

    public RollbackJournal NewJournal(params IReversibleChangeHandler[] extraHandlers) =>
        new(Store, JournalPath, [new FakeHandler(System, InstallId), .. extraHandlers], InstallId, NullLogger<RollbackJournal>.Instance);
}
