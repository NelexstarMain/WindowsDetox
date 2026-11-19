using System.Runtime.Versioning;
using System.Security.Cryptography;
using Blocker.Core.Ports;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Builds the allowlist picker's candidate list from Start Menu shortcuts, Uninstall keys, Store packages and running processes,
/// merged by executable path (case-insensitive; Store apps without a resolvable exe by package family and name) with the source flags OR-ed.
/// Store enumeration needs SYSTEM or Administrator; without it that source is silently empty. Icons are the executable's large icon (normally 32 px)
/// or the Store logo (PNG passthrough, up to 256 KB). Entries without an executable and without a package family name are dropped: they cannot be allowlisted.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class AppInventoryScanner : IAppInventoryScanner
{
    private static readonly int MaxParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Hashing stops after this long (apps after it are listed without a hash) so the picker fills promptly.</summary>
    private static readonly TimeSpan HashBudget = TimeSpan.FromSeconds(15);

    /// <summary>Executables larger than this are not hashed during a scan (games, IDEs): the path still identifies them.</summary>
    private const long MaxHashBytes = 256L * 1024 * 1024;

    private readonly Func<IReadOnlyList<ProcessInfo>> _runningProcesses;

    /// <summary>Scanner over the live process list.</summary>
    public AppInventoryScanner()
        : this(ProcessNative.EnumerateRunning)
    {
    }

    internal AppInventoryScanner(Func<IReadOnlyList<ProcessInfo>> runningProcesses) => _runningProcesses = runningProcesses;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<InstalledApp>> ScanAsync(CancellationToken cancellationToken)
    {
        var startMenu = AppSources.StartMenuAsync(cancellationToken);
        var store = AppSources.StorePackagesAsync(cancellationToken);
        var registry = Task.Run(AppSources.UninstallKeys, cancellationToken);
        var running = Task.Run(() => AppSources.Running(_runningProcesses()), cancellationToken);
        await Task.WhenAll(startMenu, store, registry, running).ConfigureAwait(false);

        // Insertion order decides the display name: Start Menu names are the most user-facing.
        var merged = Merge(startMenu.Result.Concat(store.Result).Concat(registry.Result).Concat(running.Result));
        var apps = new InstalledApp[merged.Count];
        var hashUntil = DateTime.UtcNow + HashBudget;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, merged.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
            async (index, token) => apps[index] = await EnrichAsync(merged[index], DateTime.UtcNow < hashUntil, token).ConfigureAwait(false)).ConfigureAwait(false);

        return [.. apps.OrderBy(static a => a.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Merges candidates by identity; the first name wins, later sources add flags and missing facts.</summary>
    internal static List<AppCandidate> Merge(IEnumerable<AppCandidate> candidates)
    {
        var byKey = new Dictionary<string, AppCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var key = candidate.ExecutablePath ?? (candidate.PackageFamilyName is null ? null : $"pfn:{candidate.PackageFamilyName}|{candidate.DisplayName}");
            if (key is null)
            {
                continue;
            }

            byKey[key] = byKey.TryGetValue(key, out var existing)
                ? existing with
                {
                    Source = existing.Source | candidate.Source,
                    PackageFamilyName = existing.PackageFamilyName ?? candidate.PackageFamilyName,
                    LogoPng = existing.LogoPng ?? candidate.LogoPng,
                }
                : candidate;
        }

        return [.. byKey.Values];
    }

    private static async Task<InstalledApp> EnrichAsync(AppCandidate candidate, bool hashAllowed, CancellationToken cancellationToken)
    {
        string? hash = null;
        var icon = candidate.LogoPng;
        if (candidate.ExecutablePath is { } path && File.Exists(path))
        {
            if (hashAllowed)
            {
                hash = await TryHashAsync(path, cancellationToken).ConfigureAwait(false);
            }

            icon ??= IconExtractor.TryExtractPng(path);
        }

        return new InstalledApp(candidate.DisplayName, candidate.ExecutablePath, hash, candidate.PackageFamilyName, icon, candidate.Source);
    }

    private static async Task<string?> TryHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxHashBytes)
            {
                return null;
            }

            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // locked or inaccessible: the app is still listed, just without a hash
        }
    }
}
