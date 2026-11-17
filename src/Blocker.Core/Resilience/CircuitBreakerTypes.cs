using Blocker.Sdk;

namespace Blocker.Core.Resilience;

/// <summary>State of a <see cref="PluginCircuitBreaker"/>.</summary>
public enum BreakerState
{
    /// <summary>Calls are executed.</summary>
    Closed,

    /// <summary>The plugin tripped the breaker; calls are rejected until <see cref="PluginCircuitBreaker.Reset"/>.</summary>
    Faulted,
}

/// <summary>How a guarded call ended.</summary>
public enum BreakerOutcome
{
    /// <summary>The call completed within budget.</summary>
    Success,

    /// <summary>The call exceeded its budget.</summary>
    Timeout,

    /// <summary>The call threw.</summary>
    Exception,

    /// <summary>The breaker was faulted; the call was not invoked.</summary>
    Rejected,
}

/// <summary>Result of a guarded call.</summary>
/// <param name="Value">The call's result on success; the fallback otherwise.</param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Error">The exception for <see cref="BreakerOutcome.Exception"/> or <see cref="BreakerOutcome.Timeout"/>.</param>
public readonly record struct BreakerResult<T>(T Value, BreakerOutcome Outcome, Exception? Error = null)
{
    /// <summary>Gets whether the call succeeded.</summary>
    public bool Succeeded => Outcome == BreakerOutcome.Success;
}

/// <summary>Thresholds of a <see cref="PluginCircuitBreaker"/>.</summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>Gets the time budget of one call.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromMilliseconds(BlockerSdk.CallbackBudgetMilliseconds);

    /// <summary>Gets the number of consecutive failures that trips the breaker.</summary>
    public int ConsecutiveFailuresToTrip { get; init; } = 5;

    /// <summary>Gets the number of failures within <see cref="Window"/> that trips the breaker.</summary>
    public int WindowFailuresToTrip { get; init; } = 10;

    /// <summary>Gets the sliding window for <see cref="WindowFailuresToTrip"/>.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Raised when a breaker changes state.</summary>
/// <param name="State">The new state.</param>
/// <param name="Reason">Why it faulted; <see langword="null"/> when closed.</param>
public sealed class BreakerStateChangedEventArgs(BreakerState State, string? Reason) : EventArgs
{
    /// <summary>Gets the new state.</summary>
    public BreakerState State { get; } = State;

    /// <summary>Gets why it faulted; <see langword="null"/> when closed.</summary>
    public string? Reason { get; } = Reason;
}
