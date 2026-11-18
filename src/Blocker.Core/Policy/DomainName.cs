using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Blocker.Core.Policy;

/// <summary>Host-name normalization shared by patterns, the matcher and the proxy.</summary>
internal static class DomainName
{
    /// <summary>Maximum length of a DNS name in presentation format.</summary>
    public const int MaxLength = 253;

    private const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = false };

    /// <summary>
    /// Lower-cases, drops one trailing dot, converts IDN to punycode and validates (non-empty labels of at most 63 characters, letters/digits/hyphen/underscore,
    /// no leading/trailing hyphen, at most 253 characters). IP literals (including <c>127.1</c> and hex forms) are rejected because the last label must not start with a digit.
    /// </summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        ReadOnlySpan<char> text = input.AsSpan();
        if (text[^1] == '.')
        {
            text = text[..^1];
        }

        if (text.Length is 0 or > MaxLength)
        {
            return false;
        }

        string ascii;
        if (Ascii.IsValid(text))
        {
            ascii = string.Create(text.Length, text, static (dest, src) => src.ToLowerInvariant(dest));
        }
        else
        {
            try
            {
                ascii = Idn.GetAscii(text.ToString()).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (!IsValidAscii(ascii))
        {
            return false;
        }

        normalized = ascii;
        return true;
    }

    private static bool IsValidAscii(string name)
    {
        if (name.Length is 0 or > MaxLength)
        {
            return false;
        }

        int labelStart = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] != '.')
            {
                char c = name[i];
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                {
                    return false;
                }

                continue;
            }

            int length = i - labelStart;
            if (length is 0 or > MaxLabelLength || name[labelStart] == '-' || name[i - 1] == '-')
            {
                return false;
            }

            if (i == name.Length && name[labelStart] is >= '0' and <= '9')
            {
                return false;
            }

            labelStart = i + 1;
        }

        return true;
    }

    /// <summary>Splits <c>host</c>, <c>host:port</c> or <c>[v6]:port</c>; the port is <paramref name="defaultPort"/> when absent.</summary>
    public static bool TrySplitAuthority(string authority, int defaultPort, [NotNullWhen(true)] out string? host, out int port)
    {
        host = null;
        port = defaultPort;
        if (authority.Length is 0 or > 300 || authority.AsSpan().ContainsAny("@/\\ \t"))
        {
            return false;
        }

        string portText = "";
        if (authority[0] == '[')
        {
            int end = authority.IndexOf(']');
            if (end < 0 || (end + 1 < authority.Length && authority[end + 1] != ':'))
            {
                return false;
            }

            host = authority[..(end + 1)];
            portText = end + 2 < authority.Length ? authority[(end + 2)..] : "";
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? "" : authority[(colon + 1)..];
        }

        if (portText.Length > 0 && !(int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535))
        {
            return false;
        }

        return host.Length > 0;
    }
}
