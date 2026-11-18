using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Contracts;
using Blocker.Core.Policy;
using Blocker.Core.Ports;
using Blocker.Core.Security;

namespace Blocker.Core.ServiceHost;

/// <summary>Validation and conversion of the allowlist between the wire form and the policy types.</summary>
public static class AllowlistMapping
{
    /// <summary>Largest number of applications or domains accepted.</summary>
    public const int MaxItems = 5000;

    private const int MaxTextLength = 512;

    /// <summary>Checks the allowlist the way a session would use it.</summary>
    /// <param name="apps">Applications.</param>
    /// <param name="domains">Domain patterns.</param>
    /// <returns>User-readable errors; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(IReadOnlyList<AllowlistEntryDto> apps, IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(domains);
        List<string> errors = [];
        if (apps.Count > MaxItems || domains.Count > MaxItems)
        {
            return [$"At most {MaxItems} applications and {MaxItems} domains are allowed."];
        }

        foreach (AllowlistEntryDto app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName.Length > MaxTextLength
                || (app.ExecutablePath?.Length ?? 0) > MaxTextLength || (app.PackageFamilyName?.Length ?? 0) > MaxTextLength)
            {
                errors.Add("An application has a missing or oversized name, path or package name.");
                continue;
            }

            try
            {
                _ = new Allowlist([ToEntry(app)]);
            }
            catch (ArgumentException ex)
            {
                errors.Add(ex.Message.Split('\n')[0].Replace(" (Parameter 'entries')", string.Empty, StringComparison.Ordinal));
            }
        }

        errors.AddRange(domains
            .Where(static d => d.Length > MaxTextLength || !DomainPattern.TryParse(d, out _))
            .Select(static d => $"Invalid domain pattern '{(d.Length > 64 ? d[..64] + "..." : d)}'."));
        return errors;
    }

    /// <summary>Converts a wire entry to a policy entry.</summary>
    /// <param name="app">The entry.</param>
    /// <returns>The policy entry.</returns>
    public static AllowlistEntry ToEntry(AllowlistEntryDto app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new AllowlistEntry(app.DisplayName, NullIfBlank(app.ExecutablePath), NullIfBlank(app.Sha256), NullIfBlank(app.PackageFamilyName));
    }

    /// <summary>Converts a policy entry to its wire form.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The wire entry.</returns>
    public static AllowlistEntryDto ToDto(AllowlistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AllowlistEntryDto(entry.DisplayName, entry.ExecutablePath, entry.Sha256, entry.PackageFamilyName);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The allowlist the user edits between sessions (<c>allowlist.get/set</c>); <c>session.start</c> uses it when a request names no apps.</summary>
/// <param name="store">Sealed store.</param>
/// <param name="path">File path.</param>
public sealed class DraftAllowlistStore(SecureFileStore store, string path)
{
    /// <summary>Loads the draft; empty when none was saved or the file cannot be trusted (the user simply starts over).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The draft.</returns>
    public async ValueTask<AllowlistDto> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadJsonAsync(path, IpcJson.TypeInfo<AllowlistDto>(), cancellationToken).ConfigureAwait(false) ?? new AllowlistDto([], []);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            return new AllowlistDto([], []);
        }
    }

    /// <summary>Replaces the draft.</summary>
    /// <param name="allowlist">The draft.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the draft is durable.</returns>
    public ValueTask SaveAsync(AllowlistDto allowlist, CancellationToken cancellationToken) =>
        store.WriteJsonAsync(path, allowlist, IpcJson.TypeInfo<AllowlistDto>(), cancellationToken);
}

/// <summary>Keys and permitted values of <c>settings.get/set</c>.</summary>
public static class ServiceSettingKeys
{
    /// <summary>Quality of the UI's background effect: <c>high</c>, <c>low</c> or <c>off</c>.</summary>
    public const string EffectsQuality = "effects.quality";

    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [EffectsQuality] = ["high", "low", "off"],
    };

    /// <summary>Gets the values a fresh installation has.</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EffectsQuality] = "high",
    };

    /// <summary>Returns the errors of a set of values; empty when every key is known and every value permitted.</summary>
    /// <param name="values">Values to check.</param>
    /// <returns>User-readable errors.</returns>
    public static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<string> errors = [];
        foreach ((string key, string value) in values)
        {
            if (!Allowed.TryGetValue(key, out string[]? permitted))
            {
                errors.Add($"Unknown setting '{(key.Length > 64 ? key[..64] + "..." : key)}'.");
            }
            else if (!permitted.Contains(value, StringComparer.Ordinal))
            {
                errors.Add($"'{key}' must be one of: {string.Join(", ", permitted)}.");
            }
        }

        return errors;
    }
}

/// <summary>Persists the settings in a sealed file; unreadable files fall back to the defaults.</summary>
/// <param name="store">Sealed store.</param>
/// <param name="path">File path.</param>
public sealed class ServiceSettingsStore(SecureFileStore store, string path)
{
    /// <summary>Returns the stored values on top of <see cref="ServiceSettingKeys.Defaults"/>.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>All settings.</returns>
    public async ValueTask<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> result = new(ServiceSettingKeys.Defaults, StringComparer.Ordinal);
        SettingsDto? stored;
        try
        {
            stored = await store.ReadJsonAsync(path, IpcJson.TypeInfo<SettingsDto>(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            stored = null; // a damaged settings file is not worth failing for: the defaults apply and the next write replaces it
        }

        foreach ((string key, string value) in stored?.Values.Where(static p => ServiceSettingKeys.Defaults.ContainsKey(p.Key)) ?? [])
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>Validates <paramref name="values"/> and merges them into the stored settings.</summary>
    /// <param name="values">Values to change.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Errors; empty when the values were stored.</returns>
    public async ValueTask<IReadOnlyList<string>> SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (ServiceSettingKeys.Validate(values) is { Count: > 0 } errors)
        {
            return errors;
        }

        Dictionary<string, string> merged = new(await GetAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            merged[key] = value;
        }

        await store.WriteJsonAsync(path, new SettingsDto(merged), IpcJson.TypeInfo<SettingsDto>(), cancellationToken).ConfigureAwait(false);
        return [];
    }
}

/// <summary>
/// The last application scan without icons: the coordinator needs it at start-up to plan IFEO redirects before the UI scanned anything.
/// A missing or damaged cache just means "no scan yet".
/// </summary>
/// <param name="store">Sealed store.</param>
/// <param name="path">File path.</param>
public sealed class AppScanCache(SecureFileStore store, string path)
{
    /// <summary>Loads the cached scan (empty when there is none or it cannot be trusted).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The apps.</returns>
    public async ValueTask<IReadOnlyList<InstalledApp>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            AppScanDocument? document = await store.ReadJsonAsync(path, AppScanJsonContext.Default.AppScanDocument, cancellationToken).ConfigureAwait(false);
            return document is null ? [] : [.. document.Apps.Select(static a => new InstalledApp(a.DisplayName, a.ExecutablePath, a.Sha256, a.PackageFamilyName, null, (AppSource)a.Sources))];
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Stores a scan (icons are dropped).</summary>
    /// <param name="apps">The apps.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the cache is durable.</returns>
    public ValueTask SaveAsync(IReadOnlyList<InstalledApp> apps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        AppScanDocument document = new([.. apps.Select(static a => new CachedApp(a.DisplayName, a.ExecutablePath, a.Sha256, a.PackageFamilyName, (int)a.Sources))]);
        return store.WriteJsonAsync(path, document, AppScanJsonContext.Default.AppScanDocument, cancellationToken);
    }
}

internal sealed record CachedApp(string DisplayName, string? ExecutablePath, string? Sha256, string? PackageFamilyName, int Sources);

internal sealed record AppScanDocument(List<CachedApp> Apps);

[JsonSerializable(typeof(AppScanDocument))]
internal sealed partial class AppScanJsonContext : JsonSerializerContext;
