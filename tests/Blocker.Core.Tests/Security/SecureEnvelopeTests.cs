using System.Buffers.Binary;
using System.Security.Cryptography;
using Blocker.Core.Security;

namespace Blocker.Core.Tests.Security;

public sealed class SecureEnvelopeTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Payload = "hello, rollback journal"u8.ToArray();
    private readonly FakeSecretProtector _protector = new();

    [Fact]
    public void Seal_produces_documented_layout()
    {
        byte[] blob = SecureEnvelope.Seal(Payload, _protector, Key);

        int cipherLength = Payload.Length + 4;
        Assert.Equal(9 + cipherLength + 32, blob.Length);
        Assert.Equal("BLKS"u8.ToArray(), blob[..4]);
        Assert.Equal(1, blob[4]);
        Assert.Equal(cipherLength, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(5)));
        Assert.Equal(_protector.Protect(Payload), blob[9..^32]);
        Assert.Equal(HMACSHA256.HashData(Key, blob.AsSpan(0, blob.Length - 32)), blob[^32..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(23)]
    public void Open_round_trips_payload(int length)
    {
        byte[] payload = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

        byte[] opened = SecureEnvelope.Open(SecureEnvelope.Seal(payload, _protector, Key), _protector, Key);

        Assert.Equal(payload, opened);
    }

    [Fact]
    public void Flipping_any_single_byte_is_detected_before_unprotect()
    {
        byte[] blob = SecureEnvelope.Seal(Payload, _protector, Key);

        for (int i = 0; i < blob.Length; i++)
        {
            byte[] tampered = [.. blob];
            tampered[i] ^= 0x01;
            Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(tampered, _protector, Key));
        }

        Assert.Equal(0, _protector.UnprotectCalls);
    }

    [Theory]
    [InlineData(2, "magic")]
    [InlineData(4, "version")]
    [InlineData(6, "length")]
    [InlineData(12, "MAC")]
    public void Tampered_region_reports_the_failing_check(int index, string expectedFragment)
    {
        byte[] tampered = SecureEnvelope.Seal(Payload, _protector, Key);
        tampered[index] ^= 0xFF;

        TamperDetectedException ex = Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(tampered, _protector, Key));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flipping_a_mac_byte_is_detected()
    {
        byte[] tampered = SecureEnvelope.Seal(Payload, _protector, Key);
        tampered[^1] ^= 0x80;

        TamperDetectedException ex = Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(tampered, _protector, Key));

        Assert.Equal("Envelope MAC mismatch.", ex.Message);
    }

    [Fact]
    public void Every_truncation_is_detected()
    {
        byte[] blob = SecureEnvelope.Seal(Payload, _protector, Key);

        for (int length = 0; length < blob.Length; length++)
        {
            byte[] truncated = blob[..length];
            Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(truncated, _protector, Key));
        }
    }

    [Fact]
    public void Appended_bytes_are_detected()
    {
        byte[] blob = [.. SecureEnvelope.Seal(Payload, _protector, Key), 0];

        TamperDetectedException ex = Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(blob, _protector, Key));

        Assert.Equal("Envelope length mismatch.", ex.Message);
    }

    [Fact]
    public void Wrong_key_is_detected()
    {
        byte[] blob = SecureEnvelope.Seal(Payload, _protector, Key);
        byte[] otherKey = [.. Key];
        otherKey[0] ^= 0x01;

        Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(blob, _protector, otherKey));
        Assert.Equal(0, _protector.UnprotectCalls);
    }

    [Fact]
    public void Wrong_magic_with_valid_mac_is_rejected()
    {
        byte[] blob = Resign(SecureEnvelope.Seal(Payload, _protector, Key), b => b[0] = (byte)'X');

        TamperDetectedException ex = Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(blob, _protector, Key));

        Assert.Equal("Envelope magic mismatch.", ex.Message);
    }

    [Fact]
    public void Wrong_version_with_valid_mac_is_rejected()
    {
        byte[] blob = Resign(SecureEnvelope.Seal(Payload, _protector, Key), b => b[4] = 2);

        TamperDetectedException ex = Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(blob, _protector, Key));

        Assert.Equal("Unsupported envelope version 2.", ex.Message);
    }

    [Fact]
    public void Authentic_ciphertext_the_protector_rejects_is_reported_as_tampering()
    {
        // Valid MAC over a payload this machine's protector cannot decrypt (e.g. sealed on another machine).
        byte[] foreignCipher = [1, 2, 3, 4, 5, 6];
        byte[] blob = new byte[9 + foreignCipher.Length + 32];
        "BLKS"u8.CopyTo(blob);
        blob[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(5), foreignCipher.Length);
        foreignCipher.CopyTo(blob, 9);
        HMACSHA256.HashData(Key, blob.AsSpan(0, 9 + foreignCipher.Length), blob.AsSpan(9 + foreignCipher.Length));

        TamperDetectedException ex = Assert.Throws<TamperDetectedException>(() => SecureEnvelope.Open(blob, _protector, Key));

        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    private static byte[] Resign(byte[] blob, Action<byte[]> mutateHeader)
    {
        mutateHeader(blob);
        HMACSHA256.HashData(Key, blob.AsSpan(0, blob.Length - 32), blob.AsSpan(blob.Length - 32));
        return blob;
    }
}
