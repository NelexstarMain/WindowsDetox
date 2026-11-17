using System.Buffers.Binary;
using System.Security.Cryptography;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Security;

/// <summary>
/// Tamper-evident container: <c>"BLKS" | version(1) | length(4, LE) | protected payload | HMAC-SHA256(header ‖ ciphertext)</c>.
/// The HMAC is verified (constant time) before the payload is handed to the protector.
/// </summary>
public static class SecureEnvelope
{
    private const byte Version = 1;
    private const int LengthOffset = 5;
    private const int HeaderLength = 9;
    private const int MacLength = 32;

    private static ReadOnlySpan<byte> Magic => "BLKS"u8;

    /// <summary>Protects <paramref name="payload"/> and wraps it with a keyed MAC.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> payload, ISecretProtector protector, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(hmacKey);
        byte[] cipher = protector.Protect(payload);
        byte[] blob = new byte[HeaderLength + cipher.Length + MacLength];
        Magic.CopyTo(blob);
        blob[Magic.Length] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(LengthOffset), cipher.Length);
        cipher.CopyTo(blob.AsSpan(HeaderLength));
        int signedLength = HeaderLength + cipher.Length;
        HMACSHA256.HashData(hmacKey, blob.AsSpan(0, signedLength), blob.AsSpan(signedLength));
        return blob;
    }

    /// <summary>Verifies and unprotects a blob produced by <see cref="Seal"/>.</summary>
    /// <exception cref="TamperDetectedException">Bad magic/version/length, MAC mismatch or undecryptable payload.</exception>
    public static byte[] Open(ReadOnlySpan<byte> blob, ISecretProtector protector, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(hmacKey);
        if (blob.Length < HeaderLength + MacLength)
        {
            throw new TamperDetectedException("Envelope is truncated.");
        }

        if (!blob[..Magic.Length].SequenceEqual(Magic))
        {
            throw new TamperDetectedException("Envelope magic mismatch.");
        }

        if (blob[Magic.Length] != Version)
        {
            throw new TamperDetectedException($"Unsupported envelope version {blob[Magic.Length]}.");
        }

        int cipherLength = BinaryPrimitives.ReadInt32LittleEndian(blob[LengthOffset..]);
        if (cipherLength != blob.Length - HeaderLength - MacLength)
        {
            throw new TamperDetectedException("Envelope length mismatch.");
        }

        int signedLength = HeaderLength + cipherLength;
        Span<byte> expected = stackalloc byte[MacLength];
        HMACSHA256.HashData(hmacKey, blob[..signedLength], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, blob[signedLength..]))
        {
            throw new TamperDetectedException("Envelope MAC mismatch.");
        }

        try
        {
            return protector.Unprotect(blob.Slice(HeaderLength, cipherLength));
        }
        catch (CryptographicException ex)
        {
            throw new TamperDetectedException("Envelope payload could not be unprotected.", ex);
        }
    }
}
