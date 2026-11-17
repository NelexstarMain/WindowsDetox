using System.Security.Cryptography;
using System.Text;

namespace Blocker.Core.Session;

/// <summary>Salted PBKDF2-SHA256 verifier of a break-glass code; the code itself is never stored.</summary>
/// <param name="Salt">Random 16-byte salt.</param>
/// <param name="Hash">Derived 32-byte key.</param>
/// <param name="Iterations">PBKDF2 iteration count.</param>
public sealed record BreakGlassHash(byte[] Salt, byte[] Hash, int Iterations);

/// <summary>Generates and verifies the one-time break-glass recovery code (<c>XXXXX-XXXXX-XXXXX-XXXXX</c>, 100 bits, Crockford base32).</summary>
public static class BreakGlassCode
{
    /// <summary>Default PBKDF2 iteration count.</summary>
    public const int DefaultIterations = 210_000;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 20;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    /// <summary>Generates a new code and its verifier.</summary>
    public static (string Code, BreakGlassHash Hash) Generate(int iterations = DefaultIterations)
    {
        var raw = RandomNumberGenerator.GetBytes(CodeLength);
        var plain = string.Create(CodeLength, raw, static (span, bytes) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[bytes[i] & 31]; // 256 % 32 == 0, so uniform
            }
        });

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var code = $"{plain[..5]}-{plain[5..10]}-{plain[10..15]}-{plain[15..]}";
        return (code, new BreakGlassHash(salt, Derive(plain, salt, iterations), iterations));
    }

    /// <summary>Checks <paramref name="input"/> (case-, dash- and whitespace-insensitive; Crockford aliases O, I, L accepted) in constant time.</summary>
    public static bool Verify(string? input, BreakGlassHash hash)
    {
        if (string.IsNullOrWhiteSpace(input) || hash.Iterations <= 0 || hash.Hash.Length != HashLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derive(Normalize(input), hash.Salt, hash.Iterations), hash.Hash);
    }

    private static string Normalize(string input) =>
        new([.. input.Where(static c => c != '-' && !char.IsWhiteSpace(c)).Select(static c => char.ToUpperInvariant(c) switch { 'O' => '0', 'I' or 'L' => '1', var u => u })]);

    private static byte[] Derive(string code, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, iterations, HashAlgorithmName.SHA256, HashLength);
}
