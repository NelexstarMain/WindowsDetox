using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Contracts;
using Blocker.Core.Security;

namespace Blocker.Core.AppRules;

/// <summary>The rules as stored, with the time zone their wall-clock ranges and day boundaries are read in.</summary>
/// <param name="Rules">The normalized rules.</param>
/// <param name="TimeZoneId">Zone captured from the machine when the rules were saved; <see langword="null"/> when none was saved yet.</param>
internal sealed record StoredAppRules(AppRulesDto Rules, string? TimeZoneId);

/// <summary>Persists the saved app sets in a sealed file; a missing or damaged file means no sets (the user starts over).</summary>
/// <param name="store">Sealed store.</param>
/// <param name="path">File path.</param>
internal sealed class AppSetsStore(SecureFileStore store, string path)
{
    /// <summary>Loads the sets.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The sets; empty when none were saved or the file cannot be trusted.</returns>
    public async ValueTask<AppSetsDto> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadJsonAsync(path, IpcJson.TypeInfo<AppSetsDto>(), cancellationToken).ConfigureAwait(false) ?? new AppSetsDto([]);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            return new AppSetsDto([]);
        }
    }

    /// <summary>Replaces the sets.</summary>
    /// <param name="sets">The validated sets.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the sets are durable.</returns>
    public ValueTask SaveAsync(AppSetsDto sets, CancellationToken cancellationToken) =>
        store.WriteJsonAsync(path, sets, IpcJson.TypeInfo<AppSetsDto>(), cancellationToken);
}

/// <summary>Persists the per-app rules and their frozen time zone; a missing or damaged file means no rules.</summary>
/// <param name="store">Sealed store.</param>
/// <param name="path">File path.</param>
internal sealed class AppRulesStore(SecureFileStore store, string path)
{
    /// <summary>Loads the rules.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The rules; empty when none were saved or the file cannot be trusted.</returns>
    public async ValueTask<StoredAppRules> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadJsonAsync(path, AppRuleJsonContext.Default.StoredAppRules, cancellationToken).ConfigureAwait(false)
                ?? new StoredAppRules(new AppRulesDto([]), null);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            return new StoredAppRules(new AppRulesDto([]), null);
        }
    }

    /// <summary>Replaces the rules.</summary>
    /// <param name="rules">The validated rules and their zone.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the rules are durable.</returns>
    public ValueTask SaveAsync(StoredAppRules rules, CancellationToken cancellationToken) =>
        store.WriteJsonAsync(path, rules, AppRuleJsonContext.Default.StoredAppRules, cancellationToken);
}

/// <summary>
/// Persists the usage ledger. A missing or damaged file means no usage counted so far, which is the lenient direction; the data root is
/// protected, so only an administrator can delete it.
/// </summary>
/// <param name="store">Sealed store.</param>
/// <param name="path">File path.</param>
internal sealed class AppUsageStore(SecureFileStore store, string path)
{
    /// <summary>Loads the ledger state.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The state; empty when none was saved or the file cannot be trusted.</returns>
    public async ValueTask<AppUsageState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadJsonAsync(path, AppRuleJsonContext.Default.AppUsageState, cancellationToken).ConfigureAwait(false)
                ?? new AppUsageState([]);
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            return new AppUsageState([]);
        }
    }

    /// <summary>Replaces the ledger state.</summary>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the state is durable.</returns>
    public ValueTask SaveAsync(AppUsageState state, CancellationToken cancellationToken) =>
        store.WriteJsonAsync(path, state, AppRuleJsonContext.Default.AppUsageState, cancellationToken);
}

/// <summary>Metadata for the files above; a file with missing members counts as damaged.</summary>
[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StoredAppRules))]
[JsonSerializable(typeof(AppUsageState))]
internal sealed partial class AppRuleJsonContext : JsonSerializerContext;
