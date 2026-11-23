using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

/// <summary>Real Win32 smoke tests; they only run in the Windows CI job.</summary>
[SupportedOSPlatform("windows10.0.19041")]
[Trait("Category", "WindowsOnly")]
public sealed class WindowsProcessSmokeTests
{
    private static long CurrentStartTicks() => Process.GetCurrentProcess().StartTime.ToFileTime();

    [Fact]
    public void EnumerateRunning_ContainsCurrentProcessWithMatchingFacts()
    {
        var monitor = new EtwProcessMonitor();

        var list = monitor.EnumerateRunning();

        var self = Assert.Single(list, static p => p.ProcessId == Environment.ProcessId);
        Assert.Equal(CurrentStartTicks(), self.StartTimeTicks);
        Assert.Equal(Process.GetCurrentProcess().SessionId, self.SessionId);
        Assert.Equal(Environment.ProcessPath, self.ImagePath, ignoreCase: true);
        Assert.DoesNotContain(list, static p => p.ProcessId == 0);
    }

    [Fact]
    public void DosDevices_ResolvesSystemDriveAndConvertsItsDevicePath()
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var device = DosDevices.Query(systemDrive);

        Assert.StartsWith(@"\Device\", device, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Environment.SystemDirectory + @"\cmd.exe", DosDevices.CreateConverter().Convert(device + @"\Windows\System32\cmd.exe"), ignoreCase: true);
    }

    [Fact]
    public void Controller_RefusesCriticalImages_AndNeverTerminatesTheCaller()
    {
        var controller = new Win32ProcessController(static _ => true);

        Assert.False(controller.TryTerminate(Environment.ProcessId, CurrentStartTicks()));
        Assert.Equal(Environment.ProcessId, Process.GetCurrentProcess().Id); // still alive
    }

    [Fact]
    public void Controller_RefusesWhenStartTimeDoesNotMatch()
    {
        var controller = new Win32ProcessController(static _ => false);

        Assert.False(controller.TryTerminate(Environment.ProcessId, CurrentStartTicks() + 1));
        Assert.False(controller.TryTerminate(Environment.ProcessId, 0));
    }

    [Fact]
    public void Controller_ResolvesTheImageOfALiveProcess_OnlyForItsOwnStartTime()
    {
        var controller = new Win32ProcessController(static _ => false);

        Assert.Equal(Environment.ProcessPath, controller.TryGetImagePath(Environment.ProcessId, CurrentStartTicks()), ignoreCase: true);
        Assert.Null(controller.TryGetImagePath(Environment.ProcessId, CurrentStartTicks() + 1));
        Assert.Null(controller.TryGetImagePath(int.MaxValue - 3, 1));
    }

    [Fact]
    public void Controller_ReturnsFalseForUnknownProcess() =>
        Assert.False(new Win32ProcessController(static _ => false).TryTerminate(int.MaxValue - 3, 1));

    [Fact]
    public void Controller_UnpackagedProcessHasNoPackageFamilyName() =>
        Assert.Null(new Win32ProcessController(static _ => false).GetPackageFamilyName(Environment.ProcessId));

    [Fact]
    public async Task Controller_GracefulCloseOfProcessWithoutWindowsIsFalse() =>
        Assert.False(await new Win32ProcessController(static _ => false)
            .TryCloseGracefullyAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

    [Fact]
    public void Authenticode_AcceptsExplorer()
    {
        var verifier = new AuthenticodeVerifier();
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        Assert.True(verifier.IsMicrosoftSigned(explorer));
        Assert.True(verifier.IsMicrosoftSigned(explorer)); // second call served from the cache
    }

    [Fact]
    public void Authenticode_AcceptsCatalogOrEmbeddedSignedSystemBinary()
    {
        // kernel32.dll is signed through a security catalog on most builds; embedded or catalog path must both yield true.
        Assert.True(new AuthenticodeVerifier().IsMicrosoftSigned(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
    }

    [Fact]
    public void Authenticode_RejectsUnsignedFileAndMissingFile()
    {
        var verifier = new AuthenticodeVerifier();
        var path = Path.Combine(Path.GetTempPath(), $"unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ not really an executable"));
        try
        {
            Assert.False(verifier.IsMicrosoftSigned(path));
            Assert.False(verifier.IsMicrosoftSigned(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dpapi_RoundTripsAndRejectsTampering()
    {
        var protector = new DpapiSecretProtector();
        byte[] secret = [1, 2, 3, 4, 5, 250];

        var blob = protector.Protect(secret);

        Assert.NotEqual(secret, blob);
        Assert.Equal(secret, protector.Unprotect(blob));
        blob[^1] ^= 0xFF;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(blob));
    }

    [Fact]
    public void IconExtractor_ProducesPngForExplorer()
    {
        var png = IconExtractor.TryExtractPng(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));

        Assert.NotNull(png);
        Assert.True(png.AsSpan().StartsWith(PngEncoder.Signature));
    }

    [Fact]
    public async Task AppInventory_ReturnsDedupedIdentifiableApps()
    {
        var scanner = new AppInventoryScanner();

        var apps = await scanner.ScanAsync(TestContext.Current.CancellationToken);

        Assert.All(apps, static a => Assert.True(a.ExecutablePath is not null || a.PackageFamilyName is not null));
        Assert.Equal(apps.Count, apps.Select(static a => a.ExecutablePath ?? a.DisplayName + a.PackageFamilyName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Waiter_TimesOutOnHandleThatIsNotSignaled()
    {
        using var self = ProcessNative.Open(Environment.ProcessId, global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE);

        Assert.False(await HandleWaiter.WaitAsync(self, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
    }
}
