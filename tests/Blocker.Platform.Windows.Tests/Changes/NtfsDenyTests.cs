using Blocker.Platform.Windows.Changes;

namespace Blocker.Platform.Windows.Tests.Changes;

public sealed class NtfsDenyTests
{
    private const string Root = @"C:\Program Files\WindowsDetox";
    private const string Data = @"C:\ProgramData\WindowsDetox";
    private const string OriginalSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private readonly FakeDirectoryAcl _acl = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private readonly FakeDenyTransform _transform = new();

    private NtfsDenyWriteChangeHandler Create() => new([Root, Data + "\\"], _acl, _transform);

    [Theory]
    [InlineData(@"C:\Program Files\WindowsDetox", true)]
    [InlineData(@"c:\program files\windowsdetox\", true)]
    [InlineData(@"C:\ProgramData\WindowsDetox", true)]
    [InlineData(@"C:\Program Files\WindowsDetox\sub", false)] // only the configured roots themselves
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Program Files\WindowsDetox\..\..\Windows", false)]
    [InlineData(@"C:\Program Files\WindowsDetox\..", false)]
    [InlineData(@"C:\Program Files\WindowsDetox\.", false)]
    [InlineData(@"C:/Program Files/WindowsDetox", false)]
    [InlineData(@"\\server\share\WindowsDetox", false)]
    [InlineData(@"\\?\C:\Program Files\WindowsDetox", false)]
    [InlineData(@"C:\Program Files\WindowsDetox\\", false)]
    [InlineData(@"C:\PROGRA~1\WindowsDetox", false)]
    [InlineData(@"Program Files\WindowsDetox", false)]
    [InlineData(@"C:\Program Files\WindowsDetox:stream", false)]
    [InlineData("", false)]
    public async Task Target_must_be_exactly_a_normalized_configured_root(string target, bool valid)
    {
        NtfsDenyWriteChangeHandler handler = Create();

        if (valid)
        {
            Assert.NotNull(NtfsDenyWriteChangeHandler.ValidateTarget(target, [Root.ToUpperInvariant(), Data.ToUpperInvariant()]));
            _acl.Add(Root, OriginalSddl);
            _acl.Add(Data, OriginalSddl);
            Assert.NotNull(await handler.CaptureOriginalAsync(target, _ct));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => { _ = handler.CaptureOriginalAsync(target, _ct); });
            Assert.Throws<ArgumentException>(() => { _ = handler.ApplyAsync(target, "{}", _ct); });
            Assert.Throws<ArgumentException>(() => { _ = handler.RestoreAsync(target, """{"sddl":"D:"}""", _ct); });
            Assert.Empty(_acl.Writes);
        }
    }

    [Fact]
    public void Constructor_rejects_invalid_or_empty_roots()
    {
        Assert.Throws<ArgumentException>(() => new NtfsDenyWriteChangeHandler([@"C:\a\..\b"], _acl, _transform));
        Assert.Throws<ArgumentException>(() => new NtfsDenyWriteChangeHandler(["relative"], _acl, _transform));
        Assert.Throws<ArgumentException>(() => new NtfsDenyWriteChangeHandler([], _acl, _transform));
    }

    [Fact]
    public async Task Capture_apply_restore_returns_the_exact_original_sddl()
    {
        _acl.Add(Root, OriginalSddl);
        NtfsDenyWriteChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Root, _ct);
        Assert.Equal($$"""{"sddl":"{{OriginalSddl}}"}""", original);

        await handler.ApplyAsync(Root, "{}", _ct);
        Assert.True(_transform.HasDenyAces(_acl.Directories[Root].Sddl));
        await handler.RestoreAsync(Root, original, _ct);
        int writesAfterFirstRestore = _acl.Writes.Count;
        await handler.RestoreAsync(Root, original, _ct);

        Assert.Equal(OriginalSddl, _acl.Directories[Root].Sddl);
        Assert.Equal(writesAfterFirstRestore, _acl.Writes.Count); // second restore changed nothing
    }

    [Theory]
    [InlineData("D:PAI(A;OICI;FA;;;WD)")] // Everyone full control of the install directory: code planted there would run as SYSTEM
    [InlineData("D:PAI(A;OICI;FA;;;SY)(A;OICI;0x1301bf;;;BU)")]
    [InlineData("D:PAI(A;OICI;FA;;;S-1-5-21-1-2-3-1001)")]
    [InlineData("O:BUD:PAI(A;OICI;FA;;;SY)")] // not a plain DACL
    public async Task Restore_does_not_write_a_recorded_dacl_that_newly_grants_write_to_an_unprivileged_account(string forged)
    {
        _acl.Add(Root, OriginalSddl);
        NtfsDenyWriteChangeHandler handler = Create();
        await handler.ApplyAsync(Root, "{}", _ct);

        await handler.RestoreAsync(Root, System.Text.Json.JsonSerializer.Serialize(new { sddl = forged }), _ct);

        // Only our own deny ACEs came off; nothing the journal named was written.
        Assert.Equal(OriginalSddl, _acl.Directories[Root].Sddl);
        Assert.DoesNotContain(_acl.Writes, write => write.Sddl == forged);
    }

    [Fact]
    public async Task Restore_keeps_a_recorded_ace_the_directory_already_had()
    {
        const string userAce = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;S-1-5-21-1-2-3-1001)"; // e.g. a temp directory inheriting the owner's ACE
        _acl.Add(Root, userAce);
        NtfsDenyWriteChangeHandler handler = Create();
        string? original = await handler.CaptureOriginalAsync(Root, _ct);
        await handler.ApplyAsync(Root, "{}", _ct);

        await handler.RestoreAsync(Root, original, _ct);

        Assert.Equal(userAce, _acl.Directories[Root].Sddl);
    }

    [Fact]
    public async Task Apply_twice_writes_once()
    {
        _acl.Add(Root, OriginalSddl);
        NtfsDenyWriteChangeHandler handler = Create();

        await handler.ApplyAsync(Root, "{}", _ct);
        await handler.ApplyAsync(Root, "{}", _ct);

        Assert.Single(_acl.Writes);
    }

    [Fact]
    public async Task Stale_denies_are_not_recorded_as_original()
    {
        _acl.Add(Root, _transform.AddDenyAces(OriginalSddl));

        string? original = await Create().CaptureOriginalAsync(Root, _ct);

        Assert.Contains("\"sddl\":", original);
        Assert.DoesNotContain(FakeDenyTransform.Tag, original, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reparse_points_are_refused_and_never_written()
    {
        _acl.Add(Root, OriginalSddl, reparse: true);
        NtfsDenyWriteChangeHandler handler = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.CaptureOriginalAsync(Root, _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.ApplyAsync(Root, "{}", _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.RestoreAsync(Root, """{"sddl":"D:"}""", _ct));
        Assert.Equal(0, await handler.SweepAsync("install-1", _ct));
        Assert.Empty(_acl.Writes);
    }

    [Fact]
    public async Task Missing_directory_capture_is_null_apply_throws_and_restore_is_a_no_op()
    {
        NtfsDenyWriteChangeHandler handler = Create();

        Assert.Null(await handler.CaptureOriginalAsync(Root, _ct));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await handler.ApplyAsync(Root, "{}", _ct));
        await handler.RestoreAsync(Root, """{"sddl":"D:"}""", _ct);
        await handler.RestoreAsync(Root, null, _ct);
        Assert.Empty(_acl.Writes);
    }

    [Fact]
    public async Task Sweep_removes_our_deny_aces_from_every_root_and_counts_them()
    {
        _acl.Add(Root, OriginalSddl + FakeDenyTransform.Tag + FakeDenyTransform.Tag); // two matching ACEs
        _acl.Add(Data, OriginalSddl);
        NtfsDenyWriteChangeHandler handler = Create();

        int removed = await handler.SweepAsync("any-install", _ct);

        Assert.Equal(2, removed);
        Assert.False(_transform.HasDenyAces(_acl.Directories[Root].Sddl));
        Assert.Equal(OriginalSddl, _acl.Directories[Data].Sddl);
        Assert.Equal(0, await handler.SweepAsync("any-install", _ct));
    }

    [Fact]
    public void Kind_is_stable() => Assert.Equal("ntfsdeny", Create().Kind);
}
