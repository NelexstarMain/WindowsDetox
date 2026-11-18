using Blocker.Contracts;
using Blocker.Sdk.Session;

namespace Blocker.UI.Services;

/// <summary>Event data of <see cref="IServiceConnection.NotificationReceived"/>.</summary>
/// <param name="notification">The pushed notification.</param>
internal sealed class NotificationEventArgs(NotificationDto notification) : EventArgs
{
    /// <summary>Gets the notification.</summary>
    public NotificationDto Notification { get; } = notification;
}

/// <summary>
/// The UI's view of the Blocker service: connection state, the latest pushed session snapshot and typed calls.
/// Events are raised on the UI thread. View models depend on this interface so they can be tested with a fake.
/// </summary>
internal interface IServiceConnection
{
    /// <summary>Raised when <see cref="IsConnected"/> changes.</summary>
    event EventHandler? ConnectionChanged;

    /// <summary>Raised after every session state change pushed by the service.</summary>
    event EventHandler? SessionChanged;

    /// <summary>Raised for every notification pushed by the service.</summary>
    event EventHandler<NotificationEventArgs>? NotificationReceived;

    /// <summary>Gets whether the pipe to the service is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Gets the latest known session state; idle until the first snapshot arrives.</summary>
    SessionSnapshot Session { get; }

    /// <summary>Sends a typed request.</summary>
    /// <typeparam name="TRequest">Request DTO.</typeparam>
    /// <typeparam name="TResponse">Response DTO.</typeparam>
    /// <param name="method">Method name from <see cref="IpcMethods"/>.</param>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken);

    /// <summary>Sends a typed request without parameters.</summary>
    /// <typeparam name="TResponse">Response DTO.</typeparam>
    /// <param name="method">Method name from <see cref="IpcMethods"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    ValueTask<TResponse> InvokeAsync<TResponse>(string method, CancellationToken cancellationToken);
}
