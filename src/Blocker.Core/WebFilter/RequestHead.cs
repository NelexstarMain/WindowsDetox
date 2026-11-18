using System.Buffers;
using System.Text;

namespace Blocker.Core.WebFilter;

/// <summary>A parsed proxy request head. Hop-by-hop and <c>Proxy-*</c> headers and the client's <c>Host</c> are removed from <see cref="ForwardHeaders"/>.</summary>
internal sealed class RequestHead
{
    private RequestHead(string method, string target, string? host, byte[] forwardHeaders)
    {
        Method = method;
        Target = target;
        Host = host;
        ForwardHeaders = forwardHeaders;
    }

    public string Method { get; }

    public string Target { get; }

    /// <summary>Value of the single <c>Host</c> header, or null.</summary>
    public string? Host { get; }

    /// <summary>Remaining header lines, each ending in CRLF.</summary>
    public byte[] ForwardHeaders { get; }

    /// <summary>Parses a head ending at (and including) the blank line. Returns null for anything malformed (bare LF, obs-fold, duplicate Host, bad request line).</summary>
    public static RequestHead? TryParse(ReadOnlySpan<byte> head)
    {
        int lineEnd = head.IndexOf("\r\n"u8);
        if (lineEnd <= 0 || !TryParseRequestLine(head[..lineEnd], out string? method, out string? target))
        {
            return null;
        }

        var forward = new ArrayBufferWriter<byte>();
        string? host = null;
        ReadOnlySpan<byte> rest = head[(lineEnd + 2)..];
        while (true)
        {
            int end = rest.IndexOf("\r\n"u8);
            if (end < 0)
            {
                return null;
            }

            if (end == 0)
            {
                break;
            }

            ReadOnlySpan<byte> line = rest[..end];
            rest = rest[(end + 2)..];
            int colon = line.IndexOf((byte)':');
            if (colon <= 0 || line[0] is (byte)' ' or (byte)'\t' || line.IndexOfAny((byte)'\n', (byte)'\r', (byte)0) >= 0)
            {
                return null;
            }

            ReadOnlySpan<byte> name = line[..colon];
            if (name.IndexOfAny((byte)' ', (byte)'\t') >= 0)
            {
                return null;
            }

            if (Ascii.EqualsIgnoreCase(name, "host"u8))
            {
                if (host is not null)
                {
                    return null;
                }

                host = Encoding.ASCII.GetString(line[(colon + 1)..]).Trim();
            }
            else if (!IsStripped(name))
            {
                forward.Write(line);
                forward.Write("\r\n"u8);
            }
        }

        return new RequestHead(method, target, host, forward.WrittenSpan.ToArray());
    }

    private static bool IsStripped(ReadOnlySpan<byte> name) =>
        (name.Length >= 6 && Ascii.EqualsIgnoreCase(name[..6], "proxy-"u8))
        || Ascii.EqualsIgnoreCase(name, "connection"u8)
        || Ascii.EqualsIgnoreCase(name, "keep-alive"u8)
        || Ascii.EqualsIgnoreCase(name, "upgrade"u8)
        || Ascii.EqualsIgnoreCase(name, "te"u8);

    private static bool TryParseRequestLine(ReadOnlySpan<byte> line, out string method, out string target)
    {
        method = target = "";
        int first = line.IndexOf((byte)' ');
        int last = line.LastIndexOf((byte)' ');
        if (first <= 0 || first > 16 || last <= first + 1)
        {
            return false;
        }

        ReadOnlySpan<byte> version = line[(last + 1)..];
        if (!version.SequenceEqual("HTTP/1.1"u8) && !version.SequenceEqual("HTTP/1.0"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> m = line[..first];
        ReadOnlySpan<byte> t = line[(first + 1)..last];
        foreach (byte b in m)
        {
            if (b is < (byte)'A' or > (byte)'Z')
            {
                return false;
            }
        }

        if (t.Length > 8192 || t.IndexOfAnyInRange((byte)0, (byte)0x20) >= 0 || t.Contains((byte)0x7f))
        {
            return false;
        }

        method = Encoding.ASCII.GetString(m);
        target = Encoding.ASCII.GetString(t);
        return true;
    }
}
