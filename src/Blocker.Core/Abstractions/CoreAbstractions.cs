using Blocker.Sdk.Events;

namespace Blocker.Core.Abstractions;

/// <summary>Monotonic millisecond clock (wraps <see cref="Environment.TickCount64"/>; includes sleep, resets on reboot).</summary>
public interface IMonotonicClock
{
    /// <summary>Milliseconds since boot.</summary>
    long TickCount64 { get; }
}

/// <summary>Production monotonic clock.</summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    /// <inheritdoc/>
    public long TickCount64 => Environment.TickCount64;
}

/// <summary>Publishes core events to the host event bus.</summary>
public interface IEventPublisher
{
    /// <summary>Publishes <paramref name="evt"/> to all subscribers; never throws for subscriber failures.</summary>
    ValueTask PublishAsync(IBlockerEvent evt, CancellationToken cancellationToken = default);
}

/// <summary>Machine-bound secret protection (DPAPI LocalMachine on Windows). Abstracted for Linux tests.</summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypts data produced by <see cref="Protect"/>.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Data was tampered with or produced on another machine.</exception>
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
