using System.Buffers;
using System.IO.Pipes;
using System.Security.Principal;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Blocker.Platform.Windows.Ipc;

/// <summary>
/// Establishes the role of a pipe client from facts the client cannot forge: its process (image path from the kernel) and its token
/// (via <c>RunAsClient</c>, at Identification level).
/// <list type="bullet">
/// <item><see cref="IpcCallerRole.Administrator"/>: the client token is in the Administrators group <em>and</em> elevated. A filtered
/// (non-elevated) admin token has Administrators as deny-only and does not qualify.</item>
/// <item><see cref="IpcCallerRole.Agent"/>, <see cref="IpcCallerRole.Stub"/>, <see cref="IpcCallerRole.PluginHost"/>: only when the client's
/// image is exactly <c>installDirectory\{file}</c> from the trusted-image table. The install directory must be writable by administrators only.</item>
/// <item>Everything else is <see cref="IpcCallerRole.User"/> (unless <c>trustedImagesOnly</c>, then it is refused).</item>
/// </list>
/// </summary>
public sealed partial class WindowsCallerIdentityResolver : ICallerIdentityResolver
{
    private readonly string _installDirectory;
    private readonly IReadOnlyDictionary<string, IpcCallerRole> _trustedImages;
    private readonly bool _trustedImagesOnly;
    private readonly ILogger<WindowsCallerIdentityResolver> _logger;

    /// <summary>Creates the resolver.</summary>
    /// <param name="installDirectory">Directory that contains the Blocker executables.</param>
    /// <param name="trustedImages">Executable file name (for example <c>Blocker.UI.exe</c>) to the roles that image earns. Use one table for the main
    /// pipe (Agent/Stub) and a different one for the plugin-host pipe (PluginHost).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="trustedImagesOnly">Refuse every client that is not a trusted image (use on the plugin-host pipe).</param>
    public WindowsCallerIdentityResolver(
        string installDirectory,
        IReadOnlyDictionary<string, IpcCallerRole> trustedImages,
        ILogger<WindowsCallerIdentityResolver> logger,
        bool trustedImagesOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(trustedImages);
        ArgumentNullException.ThrowIfNull(logger);
        _installDirectory = Path.GetFullPath(installDirectory);
        _trustedImages = trustedImages;
        _trustedImagesOnly = trustedImagesOnly;
        _logger = logger;
    }

    /// <inheritdoc/>
    public CallerIdentity? Resolve(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!TryGetClientProcessId(pipe, out var processId))
        {
            return null;
        }

        var imagePath = TryGetImagePath(processId);
        var trusted = TrustedRolesOf(imagePath);

        string? sid = null;
        var elevatedAdministrator = false;
        pipe.RunAsClient(() => (sid, elevatedAdministrator) = InspectClientToken());

        // A pid can only be recycled after the client exited, which closes the pipe: reject if the client is gone.
        if (!pipe.IsConnected)
        {
            return null;
        }

        var roles = trusted;
        if (!_trustedImagesOnly)
        {
            roles |= IpcCallerRole.User;
            if (elevatedAdministrator)
            {
                roles |= IpcCallerRole.Administrator;
            }
        }

        if (roles == IpcCallerRole.None)
        {
            LogRefused(_logger, processId, imagePath);
            return null;
        }

        return new CallerIdentity(roles, processId, imagePath, sid);
    }

    private static bool TryGetClientProcessId(NamedPipeServerStream pipe, out int processId)
    {
        if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            processId = 0;
            return false;
        }

        processId = (int)pid;
        return true;
    }

    private static (string? Sid, bool ElevatedAdministrator) InspectClientToken()
    {
        using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (identity is null)
        {
            return (null, false);
        }

        // IsInRole ignores deny-only SIDs, so it is false for a filtered admin token.
        var inAdministrators = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return (identity.User?.Value, inAdministrators && IsElevated(identity));
    }

    private static unsafe bool IsElevated(WindowsIdentity identity)
    {
        TOKEN_ELEVATION elevation = default;
        return PInvoke.GetTokenInformation(identity.AccessToken, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), out _)
            && elevation.TokenIsElevated != 0;
    }

    private static string? TryGetImagePath(int processId)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var buffer = ArrayPool<char>.Shared.Rent(32768);
        try
        {
            var length = (uint)buffer.Length;
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer.AsSpan(), ref length)
                ? new string(buffer, 0, (int)length)
                : null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private IpcCallerRole TrustedRolesOf(string? imagePath)
    {
        if (imagePath is null)
        {
            return IpcCallerRole.None;
        }

        var directory = Path.GetDirectoryName(imagePath);
        var file = Path.GetFileName(imagePath);
        if (directory is null
            || !string.Equals(Path.GetFullPath(directory), _installDirectory, StringComparison.OrdinalIgnoreCase)
            || !_trustedImages.TryGetValue(file, out var roles))
        {
            return IpcCallerRole.None;
        }

        return roles;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipe client refused: pid {ProcessId} ({ImagePath}) is not a trusted image.")]
    private static partial void LogRefused(ILogger logger, int processId, string? imagePath);
}
