using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocker.Contracts;

/// <summary>Stable error codes of the IPC protocol. Numeric values and names never change.</summary>
public enum IpcErrorCode
{
    /// <summary>The message is malformed or not allowed in this position.</summary>
    InvalidRequest = 1,

    /// <summary>The caller's role may not call the method.</summary>
    Unauthorized = 2,

    /// <summary>The method may not be called while a session runs.</summary>
    NotAllowedDuringSession = 3,

    /// <summary>The parameters are well-formed but not acceptable.</summary>
    ValidationFailed = 4,

    /// <summary>Unknown method or object.</summary>
    NotFound = 5,

    /// <summary>Unexpected failure; details are only logged by the server.</summary>
    Internal = 6,

    /// <summary>The message exceeds the limit of the method or the connection.</summary>
    PayloadTooLarge = 7,

    /// <summary>The caller sent too many requests.</summary>
    RateLimited = 8,
}

/// <summary>Error of a failed request.</summary>
/// <param name="Code">Stable code.</param>
/// <param name="Message">Generic, user-safe message. Never contains stack traces or internal details.</param>
public sealed record IpcError(IpcErrorCode Code, string Message);

/// <summary>Kind of an <see cref="IpcEnvelope"/>, derived from which members are set.</summary>
public enum IpcMessageKind
{
    /// <summary>Not a valid combination of members.</summary>
    Invalid,

    /// <summary>Has an id and a method; expects a response.</summary>
    Request,

    /// <summary>Has a method but no id; no response.</summary>
    Notification,

    /// <summary>Has no method and exactly one of result or error.</summary>
    Response,
}

/// <summary>
/// One IPC message in JSON-RPC style. A request carries <see cref="Id"/> + <see cref="Method"/> (+ <see cref="Params"/>),
/// a notification carries <see cref="Method"/> only, a response carries <see cref="Id"/> and exactly one of
/// <see cref="Result"/> or <see cref="Error"/>. A response with a <see langword="null"/> id and an error is a
/// connection-level error after which the sender closes the connection.
/// </summary>
/// <param name="Id">Correlation id chosen by the requester.</param>
/// <param name="Method">Method name (see <see cref="IpcMethods"/>).</param>
/// <param name="Params">Request parameters (a JSON object or absent).</param>
/// <param name="Result">Successful result.</param>
/// <param name="Error">Failure.</param>
public sealed record IpcEnvelope(long? Id, string? Method, JsonElement? Params, JsonElement? Result, IpcError? Error)
{
    /// <summary>Gets the message kind.</summary>
    [JsonIgnore]
    public IpcMessageKind Kind
    {
        get
        {
            if (Method is not null)
            {
                if (Result is not null || Error is not null)
                {
                    return IpcMessageKind.Invalid;
                }

                return Id is null ? IpcMessageKind.Notification : IpcMessageKind.Request;
            }

            if (Params is not null || (Result is null) == (Error is null))
            {
                return IpcMessageKind.Invalid;
            }

            // Only an error may lack an id (connection-level failure).
            return Id is null && Error is null ? IpcMessageKind.Invalid : IpcMessageKind.Response;
        }
    }

    /// <summary>Creates a request.</summary>
    /// <param name="id">Correlation id.</param>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <returns>The envelope.</returns>
    public static IpcEnvelope Request(long id, string method, JsonElement? parameters) => new(id, method, parameters, null, null);

    /// <summary>Creates a notification.</summary>
    /// <param name="method">Method name.</param>
    /// <param name="parameters">Parameters, or <see langword="null"/>.</param>
    /// <returns>The envelope.</returns>
    public static IpcEnvelope Notification(string method, JsonElement? parameters) => new(null, method, parameters, null, null);

    /// <summary>Creates a successful response.</summary>
    /// <param name="id">Id of the request.</param>
    /// <param name="result">Result.</param>
    /// <returns>The envelope.</returns>
    public static IpcEnvelope Success(long id, JsonElement result) => new(id, null, null, result, null);

    /// <summary>Creates a failed response; <paramref name="id"/> is <see langword="null"/> for connection-level errors.</summary>
    /// <param name="id">Id of the request, if known.</param>
    /// <param name="code">Error code.</param>
    /// <param name="message">Generic message.</param>
    /// <returns>The envelope.</returns>
    public static IpcEnvelope Failure(long? id, IpcErrorCode code, string message) => new(id, null, null, null, new IpcError(code, message));
}

/// <summary>Thrown by handlers (server side) to return a deliberate error, and by the client when a call fails.</summary>
public sealed class IpcException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Message that is safe to show to the peer.</param>
    public IpcException(IpcErrorCode code, string message)
        : base(message) => Code = code;

    /// <summary>Gets the error code.</summary>
    public IpcErrorCode Code { get; }
}
