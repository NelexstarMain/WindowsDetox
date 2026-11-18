using Blocker.Platform.Windows.Changes;
using Microsoft.Win32;

namespace Blocker.Platform.Windows.Tests.Changes;

/// <summary>
/// Real-registry round trips. IFEO writes HKLM (needs an elevated token, hence <c>RequiresAdmin</c>; the Windows CI runner
/// is elevated). Policies run against HKCU\Software\WindowsDetoxTests and need no elevation. These tests fail (not skip)
/// when the required rights are missing.
/// </summary>
[Trait("Category", "WindowsOnly")]
public sealed class RegistryIntegrationTests
{
    private const string Dummy = "blocker-test-dummy.exe";
    private const string TestRoot = @"Software\WindowsDetoxTests";
    private const string Chrome = TestRoot + @"\Policies\Google\Chrome";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string? ReadIfeo(RegistryView view, string value)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using RegistryKey? key = root.OpenSubKey($@"{RegistryIfeoStore.DefaultBasePath}\{Dummy}");
        return key?.GetValue(value) as string;
    }

    private static bool IfeoKeyExists(RegistryView view)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using RegistryKey? key = root.OpenSubKey($@"{RegistryIfeoStore.DefaultBasePath}\{Dummy}");
        return key is not null;
    }

    [Fact]
    [Trait("Category", "RequiresAdmin")]
    public async Task Ifeo_apply_then_restore_leaves_no_trace()
    {
        var handler = new IfeoChangeHandler("test-install", ["explorer.exe"]);
        try
        {
            string? original = await handler.CaptureOriginalAsync(Dummy, _ct);
            Assert.False(IfeoKeyExists(RegistryView.Registry64));

            await handler.ApplyAsync(Dummy, """{"stubPath":"C:\\Windows\\System32\\notepad.exe"}""", _ct);
            Assert.Equal("\"C:\\Windows\\System32\\notepad.exe\" --ifeo", ReadIfeo(RegistryView.Registry64, "Debugger"));
            Assert.Equal("test-install", ReadIfeo(RegistryView.Registry64, PlatformMarker.MarkerValueName));

            await handler.RestoreAsync(Dummy, original, _ct);
            await handler.RestoreAsync(Dummy, original, _ct); // idempotent

            Assert.False(IfeoKeyExists(RegistryView.Registry64));
            Assert.False(IfeoKeyExists(RegistryView.Registry32));
        }
        finally
        {
            await handler.SweepAsync("test-install", _ct);
        }
    }

    [Fact]
    public async Task Policy_values_round_trip_and_sweep_in_the_test_hive()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        var handler = new RegistryPolicyChangeHandler("test-install", RegistryHive.CurrentUser, [Chrome]);
        try
        {
            using (RegistryKey seed = Registry.CurrentUser.CreateSubKey(Chrome))
            {
                seed.SetValue("QuicAllowed", 1, RegistryValueKind.DWord);
            }

            string? quicOriginal = await handler.CaptureOriginalAsync($"{Chrome}|QuicAllowed", _ct);
            string? proxyOriginal = await handler.CaptureOriginalAsync($"{Chrome}|ProxySettings", _ct);

            await handler.ApplyAsync($"{Chrome}|QuicAllowed", """{"kind":"DWord","number":0}""", _ct);
            await handler.ApplyAsync($"{Chrome}|ProxySettings", """{"kind":"String","text":"{}"}""", _ct);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Chrome)!)
            {
                Assert.Equal(0, key.GetValue("QuicAllowed"));
                Assert.Equal("{}", key.GetValue("ProxySettings"));
                Assert.Equal("test-install", key.GetValue("BlockerOwned.QuicAllowed"));
            }

            await handler.RestoreAsync($"{Chrome}|ProxySettings", proxyOriginal, _ct);
            await handler.RestoreAsync($"{Chrome}|QuicAllowed", quicOriginal, _ct);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Chrome)!)
            {
                Assert.Equal(1, key.GetValue("QuicAllowed"));
                Assert.Equal(["QuicAllowed"], key.GetValueNames());
            }

            await handler.ApplyAsync($"{Chrome}|ProxySettings", """{"kind":"String","text":"{}"}""", _ct);
            Assert.Equal(1, await handler.SweepAsync("test-install", _ct));
            using RegistryKey after = Registry.CurrentUser.OpenSubKey(Chrome)!;
            Assert.Equal(["QuicAllowed"], after.GetValueNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        }
    }
}
