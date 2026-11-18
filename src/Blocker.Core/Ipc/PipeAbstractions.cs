using System.IO.Pipes;
using Blocker.Contracts;

namespace Blocker.Core.Ipc;

/// <summary>Who is on the other end of a pipe, as established by the server (never by the client).</summary>
/// <param name="Roles">Roles granted to the caller; must not be <see cref="IpcCallerRole.None"/>.</param>
/// <param name="ProcessId">Client process id, if known.</param>
/// <param name="ImagePath">Client image path, if known.</param>
/// <param name="UserSid">Client user SID, if known.</param>
public sealed record CallerIdentity(IpcCallerRole Roles, int? ProcessId = null, string? ImagePath = null, string? UserSid = null);

/// <summary>Determines the roles of a connected pipe client. Windows resolves them from the client's token and image.</summary>
public interface ICallerIdentityResolver
{
    /// <summary>Resolves the identity of the client connected to <paramref name="pipe"/>; called once per connection, right after it was accepted.</summary>
    /// <param name="pipe">The connected server end.</param>
    /// <returns>The identity, or <see langword="null"/> to refuse the connection.</returns>
    CallerIdentity? Resolve(NamedPipeServerStream pipe);
}

/// <summary>Creates listening pipe instances; Windows supplies ACLs, first-instance protection and integrity labels.</summary>
public interface IPipeListenerFactory
{
    /// <summary>Creates one listening (not yet connected) instance.</summary>
    /// <param name="pipeName">Pipe name without the <c>\\.\pipe\</c> prefix.</param>
    /// <param name="isFirstInstance"><see langword="true"/> for the very first instance of the server, which must fail if the name already exists (anti-squatting).</param>
    /// <returns>The listening stream; must be asynchronous.</returns>
    NamedPipeServerStream Create(string pipeName, bool isFirstInstance);
}

/// <summary>
/// Cross-platform listener restricted to the current user (Unix sockets on Linux). Used by tests and non-Windows hosts;
/// Windows production hosts use the ACL-secured factory in <c>Blocker.Platform.Windows</c>.
/// </summary>
public sealed class DefaultPipeListenerFactory : IPipeListenerFactory
{
    /// <inheritdoc/>
    public NamedPipeServerStream Create(string pipeName, bool isFirstInstance)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (isFirstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options);
    }
}
