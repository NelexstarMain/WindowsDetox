using System.Security.Cryptography;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Tests.Security;

/// <summary>Test protector: XOR with a constant and a trailing tag, so foreign or altered ciphertext is rejected.</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    private const byte Mask = 0x5A;
    private static readonly byte[] Tag = "FAKE"u8.ToArray();

    /// <summary>Number of <see cref="Unprotect"/> calls.</summary>
    public int UnprotectCalls { get; private set; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] result = new byte[plaintext.Length + Tag.Length];
        for (int i = 0; i < plaintext.Length; i++)
        {
            result[i] = (byte)(plaintext[i] ^ Mask);
        }

        Tag.CopyTo(result, plaintext.Length);
        return result;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        UnprotectCalls++;
        int length = ciphertext.Length - Tag.Length;
        if (length < 0 || !ciphertext[length..].SequenceEqual(Tag))
        {
            throw new CryptographicException("Fake protector tag mismatch.");
        }

        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = (byte)(ciphertext[i] ^ Mask);
        }

        return result;
    }
}
