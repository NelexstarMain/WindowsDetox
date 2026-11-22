using System.IO.Abstractions;
using System.Security.Cryptography;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Security;

/// <summary>
/// Loads or creates the 32-byte HMAC key, persisted protected by the machine secret protector.
/// A missing key file is created. A file that still cannot be unprotected after a few immediate retries (DPAPI can fail
/// transiently) or has the wrong size is moved aside to <c>hmac.key.&lt;UTC yyyyMMddHHmmss&gt;.bad</c>, never deleted, and a fresh
/// key is created; envelopes sealed with the lost key then fail verification, which callers treat as tampering (marker sweep).
/// I/O and access failures propagate: the key must never be regenerated because the disk was momentarily unreadable.
/// </summary>
public sealed class HmacKeyStore(IFileSystem fileSystem, ISecretProtector protector, string keyPath)
{
    private const int KeyLength = 32;
    private const int UnprotectAttempts = 3;
    private readonly Lock _gate = new();
    private byte[]? _key;

    /// <summary>Returns a copy of the key, creating and persisting it on first use.</summary>
    public byte[] GetKey()
    {
        lock (_gate)
        {
            return [.. _key ??= LoadOrCreate()];
        }
    }

    private byte[] LoadOrCreate()
    {
        if (TryLoad() is { } existing)
        {
            return existing;
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        AtomicFile.Write(fileSystem, keyPath, protector.Protect(key));
        return key;
    }

    private byte[]? TryLoad()
    {
        if (!fileSystem.File.Exists(keyPath))
        {
            return null;
        }

        byte[] protectedKey = fileSystem.File.ReadAllBytes(keyPath);
        for (int attempt = 0; attempt < UnprotectAttempts; attempt++)
        {
            if (TryUnprotect(protectedKey) is { } key)
            {
                if (key.Length == KeyLength)
                {
                    return key;
                }

                break;
            }
        }

        fileSystem.File.Move(keyPath, $"{keyPath}.{DateTime.UtcNow:yyyyMMddHHmmss}.bad");
        return null;
    }

    private byte[]? TryUnprotect(byte[] protectedKey)
    {
        try
        {
            return protector.Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
