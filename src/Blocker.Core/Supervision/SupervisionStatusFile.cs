using System.Text.Json;
using System.Text.Json.Serialization;
using Blocker.Core.Security;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Supervision;

/// <summary>What the service tells the watchdog: whether a session is running, so supervision is needed.</summary>
/// <param name="SessionActive">Whether a session (or emergency pass) is running.</param>
/// <param name="SessionId">Id of the running session.</param>
/// <param name="UpdatedAt">When the service wrote the file (diagnostics only).</param>
public sealed record SupervisionStatus(bool SessionActive, Guid? SessionId, DateTimeOffset UpdatedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4)]
[JsonSerializable(typeof(SupervisionStatus))]
internal sealed partial class SupervisionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The small status file the service writes into ProgramData and the watchdog reads. It is sealed with <see cref="SecureFileStore"/>
/// (machine-bound protection plus HMAC), so a user cannot forge it; the ProgramData ACL keeps users from replacing or deleting it.
/// A damaged or forged file is treated as "session active": tampering can only add supervision, never remove it.
/// A missing file means nothing was ever written, which is the state of a fresh installation.
/// </summary>
/// <param name="store">Sealed file store.</param>
/// <param name="path">Path of the status file.</param>
/// <param name="logger">Logger.</param>
public sealed partial class SupervisionStatusFile(SecureFileStore store, string path, ILogger<SupervisionStatusFile> logger)
{
    /// <summary>Writes the status atomically (service side).</summary>
    /// <param name="status">The status.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the file is durable.</returns>
    public ValueTask WriteAsync(SupervisionStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        return store.WriteJsonAsync(path, status, SupervisionJsonContext.Default.SupervisionStatus, cancellationToken);
    }

    /// <summary>Returns whether the partner must be supervised right now (watchdog side).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> while a session is active or when the file cannot be trusted.</returns>
    public async ValueTask<bool> ShouldSuperviseAsync(CancellationToken cancellationToken)
    {
        try
        {
            SupervisionStatus? status = await store.ReadJsonAsync(path, SupervisionJsonContext.Default.SupervisionStatus, cancellationToken).ConfigureAwait(false);
            return status?.SessionActive ?? false;
        }
        catch (Exception ex) when (ex is TamperDetectedException or JsonException)
        {
            LogUntrusted(logger, ex.GetType().Name);
            return true;
        }
        catch (IOException ex)
        {
            // A half-replaced file during a write: keep supervising rather than guess.
            LogUntrusted(logger, ex.GetType().Name);
            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Supervision status file is not trustworthy ({Reason}); supervising.")]
    private static partial void LogUntrusted(ILogger logger, string reason);
}
