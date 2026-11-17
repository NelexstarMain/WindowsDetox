using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Blocker.Core.Session;

namespace Blocker.Core.Tests.Session;

public sealed partial class BreakGlassCodeTests
{
    private const int Iterations = 1_000;

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}$")]
    private static partial Regex CodeFormat();

    [Fact]
    public void Generate_ProducesFormattedCodeAndSaltedHash()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);

        Assert.Matches(CodeFormat(), code);
        Assert.Equal(23, code.Length);
        Assert.Equal(16, hash.Salt.Length);
        Assert.Equal(32, hash.Hash.Length);
        Assert.Equal(Iterations, hash.Iterations);
    }

    [Fact]
    public void Generate_DefaultIterationsAre210000()
    {
        Assert.Equal(210_000, BreakGlassCode.DefaultIterations);
        Assert.Equal(210_000, BreakGlassCode.Generate().Hash.Iterations);
    }

    [Fact]
    public void Generate_CodesAndSaltsAreUnique()
    {
        var generated = Enumerable.Range(0, 25).Select(_ => BreakGlassCode.Generate(Iterations)).ToList();

        Assert.Equal(25, generated.Select(g => g.Code).Distinct().Count());
        Assert.Equal(25, generated.Select(g => Convert.ToHexString(g.Hash.Salt)).Distinct().Count());
    }

    [Fact]
    public void Generate_UsesWholeAlphabetOverManyCodes()
    {
        var chars = Enumerable.Range(0, 200).SelectMany(_ => BreakGlassCode.Generate(1).Code).Where(c => c != '-').ToHashSet();

        Assert.Equal(32, chars.Count);
    }

    [Fact]
    public void Verify_ExactCode_Succeeds()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);

        Assert.True(BreakGlassCode.Verify(code, hash));
    }

    [Fact]
    public void Verify_IsCaseDashAndWhitespaceInsensitive()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);
        var compact = code.Replace("-", string.Empty, StringComparison.Ordinal);

        Assert.True(BreakGlassCode.Verify(code.ToLowerInvariant(), hash));
        Assert.True(BreakGlassCode.Verify(compact, hash));
        Assert.True(BreakGlassCode.Verify(compact.ToLowerInvariant(), hash));
        Assert.True(BreakGlassCode.Verify(code.Replace('-', ' '), hash));
        Assert.True(BreakGlassCode.Verify($"  {code}\r\n", hash));
        Assert.True(BreakGlassCode.Verify(string.Join(" - ", code.Split('-')), hash));
        Assert.True(BreakGlassCode.Verify(compact[..5] + "--" + compact[5..], hash));
    }

    [Fact]
    public void Verify_WrongCode_Fails()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);
        var (other, _) = BreakGlassCode.Generate(Iterations);
        var flipped = (code[0] == 'A' ? 'B' : 'A') + code[1..];

        Assert.False(BreakGlassCode.Verify(other, hash));
        Assert.False(BreakGlassCode.Verify(flipped, hash));
        Assert.False(BreakGlassCode.Verify(code[..^1], hash));
        Assert.False(BreakGlassCode.Verify(code + "A", hash));
        Assert.False(BreakGlassCode.Verify("AAAAA-AAAAA-AAAAA-AAAAA", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-----")]
    public void Verify_EmptyInput_Fails(string? input)
    {
        var (_, hash) = BreakGlassCode.Generate(Iterations);

        Assert.False(BreakGlassCode.Verify(input, hash));
    }

    [Fact]
    public void Verify_CrockfordAliases_AreAccepted()
    {
        const string plain = "0123456789ABCDEFGHJK";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = new BreakGlassHash(salt, Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(plain), salt, Iterations, HashAlgorithmName.SHA256, 32), Iterations);

        Assert.True(BreakGlassCode.Verify("01234-56789-ABCDE-FGHJK", hash));
        Assert.True(BreakGlassCode.Verify("O1234-56789-ABCDE-FGHJK", hash)); // O -> 0
        Assert.True(BreakGlassCode.Verify("o1234-56789-abcde-fghjk", hash));
        Assert.True(BreakGlassCode.Verify("0I234-56789-ABCDE-FGHJK", hash)); // I -> 1
        Assert.True(BreakGlassCode.Verify("0l234-56789-ABCDE-FGHJK", hash)); // L -> 1
        Assert.False(BreakGlassCode.Verify("02234-56789-ABCDE-FGHJK", hash));
    }

    [Fact]
    public void Verify_HashWithOtherSalt_Fails()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);
        var (_, other) = BreakGlassCode.Generate(Iterations);

        Assert.False(BreakGlassCode.Verify(code, hash with { Salt = other.Salt }));
    }

    [Fact]
    public void Verify_DifferentIterationCount_Fails()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);

        Assert.False(BreakGlassCode.Verify(code, hash with { Iterations = Iterations + 1 }));
    }

    [Fact]
    public void Verify_MalformedHash_Fails()
    {
        var (code, hash) = BreakGlassCode.Generate(Iterations);

        Assert.False(BreakGlassCode.Verify(code, hash with { Iterations = 0 }));
        Assert.False(BreakGlassCode.Verify(code, hash with { Hash = hash.Hash[..16] }));
    }
}
