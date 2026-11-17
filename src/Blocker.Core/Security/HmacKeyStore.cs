using System.IO.Abstractions;
using System.Security.Cryptography;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Security;

/// <summary>
/// Loads or creates the 32-byte HMAC key, persisted protected by the machine secret protector.
/// A missing, unreadable or wrong-sized key file is replaced by a fresh key; envelopes sealed with the lost key then fail
/// verification, which callers treat as tampering (marker sweep).
/// </summary>
public sealed class HmacKeyStore(IFileSystem fileSystem, ISecretProtector protector, string keyPath)
{
    private const int KeyLength = 32;
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

        try
        {
            byte[] key = protector.Unprotect(fileSystem.File.ReadAllBytes(keyPath));
            return key.Length == KeyLength ? key : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
