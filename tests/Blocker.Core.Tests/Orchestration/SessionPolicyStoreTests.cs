using System.IO.Abstractions.TestingHelpers;
using Blocker.Core.Orchestration;
using Blocker.Core.Policy;
using Blocker.Core.Security;
using Blocker.Core.Tests.Journal;

namespace Blocker.Core.Tests.Orchestration;

public sealed class SessionPolicyStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SessionPolicy Policy = new(
        [new AllowlistEntry("Editor", @"C:\Apps\editor.exe"), new AllowlistEntry("Store", null, null, "Pkg_1")],
        ["example.com", "*.docs.example.com"]);

    [Fact]
    public async Task InMemory_RoundTripsAndClears()
    {
        var store = new InMemorySessionPolicyStore();
        Assert.Null(await store.LoadAsync(Ct));
        await store.SaveAsync(Policy, Ct);
        Assert.Same(Policy, await store.LoadAsync(Ct));
        await store.ClearAsync(Ct);
        Assert.Null(await store.LoadAsync(Ct));
    }

    [Fact]
    public async Task Secure_RoundTripsThroughTheSealedFile()
    {
        var h = new JournalHarness();
        string path = MockUnixSupport.Path(@"C:\ProgramData\Blocker\policy.bin");
        var store = new SecureSessionPolicyStore(h.Store, path);
        Assert.Null(await store.LoadAsync(Ct));

        await store.SaveAsync(Policy, Ct);
        SessionPolicy? loaded = await store.LoadAsync(Ct);

        Assert.NotNull(loaded);
        Assert.Equal(Policy.Entries, loaded.Entries);
        Assert.Equal(Policy.Domains, loaded.Domains);
        Assert.True(h.Fs.File.Exists(path));

        await store.ClearAsync(Ct);
        Assert.False(h.Fs.File.Exists(path));
        Assert.Null(await store.LoadAsync(Ct));
    }

    [Fact]
    public async Task Secure_DetectsTampering()
    {
        var h = new JournalHarness();
        string path = MockUnixSupport.Path(@"C:\ProgramData\Blocker\policy.bin");
        var store = new SecureSessionPolicyStore(h.Store, path);
        await store.SaveAsync(Policy, Ct);
        byte[] bytes = h.Fs.File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        h.Fs.File.WriteAllBytes(path, bytes);

        await Assert.ThrowsAsync<TamperDetectedException>(async () => await store.LoadAsync(Ct));
    }
}
