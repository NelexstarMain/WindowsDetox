using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Blocker.Core.WebFilter;

/// <summary>Result of parsing the start of a TLS connection.</summary>
public enum ClientHelloStatus
{
    /// <summary>More bytes are needed (callers may read up to <see cref="TlsClientHelloParser.MaxRecordSize"/>).</summary>
    NeedMoreData,

    /// <summary>Not a (supported) TLS ClientHello, or malformed.</summary>
    Invalid,

    /// <summary>A complete ClientHello without a host_name server_name extension.</summary>
    NoSni,

    /// <summary>A complete ClientHello carrying a host name.</summary>
    Found,
}

/// <summary>
/// Bounds-checked SNI extraction from a TLS ClientHello. Never throws. Only a ClientHello contained in a single TLS record is supported
/// (all mainstream clients); a hello fragmented over several records is reported as <see cref="ClientHelloStatus.Invalid"/> (fail closed).
/// A duplicated server_name extension or host_name entry is rejected to prevent smuggling a second name.
/// </summary>
public static class TlsClientHelloParser
{
    /// <summary>Largest plaintext TLS record; callers should stop reading here.</summary>
    public const int MaxRecordSize = 16384 + 5;

    /// <summary>True when a complete ClientHello with a host name was found; false for partial, invalid or SNI-less data.</summary>
    public static bool TryGetSni(ReadOnlySpan<byte> data, [NotNullWhen(true)] out string? serverName) =>
        Parse(data, out serverName) == ClientHelloStatus.Found;

    /// <summary>Parses the beginning of a connection.</summary>
    public static ClientHelloStatus Parse(ReadOnlySpan<byte> data, out string? serverName)
    {
        serverName = null;
        if (data.Length < 5)
        {
            return data.Length > 0 && (data[0] != 0x16 || (data.Length > 1 && data[1] != 3)) ? ClientHelloStatus.Invalid : ClientHelloStatus.NeedMoreData;
        }

        if (data[0] != 0x16 || data[1] != 3)
        {
            return ClientHelloStatus.Invalid;
        }

        int recordLength = (data[3] << 8) | data[4];
        if (recordLength is < 4 or > 16384)
        {
            return ClientHelloStatus.Invalid;
        }

        ReadOnlySpan<byte> record = data[5..];
        if (record.Length < 4)
        {
            return ClientHelloStatus.NeedMoreData;
        }

        if (record[0] != 1)
        {
            return ClientHelloStatus.Invalid;
        }

        int helloLength = (record[1] << 16) | (record[2] << 8) | record[3];
        if (helloLength + 4 > recordLength)
        {
            return ClientHelloStatus.Invalid;
        }

        if (record.Length < helloLength + 4)
        {
            return ClientHelloStatus.NeedMoreData;
        }

        ClientHelloStatus status = ParseHello(record.Slice(4, helloLength), out serverName);
        if (status != ClientHelloStatus.Found)
        {
            serverName = null;
        }

        return status;
    }

    private static ClientHelloStatus ParseHello(ReadOnlySpan<byte> hello, out string? serverName)
    {
        serverName = null;
        var r = new Reader(hello);
        if (!r.Skip(2 + 32) || !r.TryReadU8(out int sessionIdLength) || sessionIdLength > 32 || !r.Skip(sessionIdLength)
            || !r.TryReadU16(out int suitesLength) || suitesLength < 2 || (suitesLength & 1) != 0 || !r.Skip(suitesLength)
            || !r.TryReadU8(out int compressionLength) || compressionLength < 1 || !r.Skip(compressionLength))
        {
            return ClientHelloStatus.Invalid;
        }

        if (r.Remaining == 0)
        {
            return ClientHelloStatus.NoSni;
        }

        if (!r.TryReadU16(out int extensionsLength) || extensionsLength != r.Remaining)
        {
            return ClientHelloStatus.Invalid;
        }

        bool seenServerName = false;
        while (r.Remaining > 0)
        {
            if (!r.TryReadU16(out int type) || !r.TryReadU16(out int length) || !r.TryRead(length, out ReadOnlySpan<byte> body))
            {
                return ClientHelloStatus.Invalid;
            }

            if (type != 0)
            {
                continue;
            }

            if (seenServerName || !TryParseServerNameList(body, out serverName))
            {
                serverName = null;
                return ClientHelloStatus.Invalid;
            }

            seenServerName = true;
        }

        return serverName is null ? ClientHelloStatus.NoSni : ClientHelloStatus.Found;
    }

    private static bool TryParseServerNameList(ReadOnlySpan<byte> body, out string? name)
    {
        name = null;
        var r = new Reader(body);
        if (!r.TryReadU16(out int listLength) || listLength != r.Remaining)
        {
            return false;
        }

        while (r.Remaining > 0)
        {
            if (!r.TryReadU8(out int nameType) || !r.TryReadU16(out int length) || !r.TryRead(length, out ReadOnlySpan<byte> value))
            {
                return false;
            }

            if (nameType != 0)
            {
                continue;
            }

            if (name is not null || value.Length is 0 or > 255 || !IsHostName(value))
            {
                return false;
            }

            name = Encoding.ASCII.GetString(value);
        }

        return true;
    }

    private static bool IsHostName(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'.' or (byte)'_'))
            {
                return false;
            }
        }

        return true;
    }

    private ref struct Reader(ReadOnlySpan<byte> span)
    {
        private ReadOnlySpan<byte> _rest = span;

        public readonly int Remaining => _rest.Length;

        public bool Skip(int count) => TryRead(count, out _);

        public bool TryRead(int count, out ReadOnlySpan<byte> value)
        {
            if (count > _rest.Length)
            {
                value = default;
                return false;
            }

            value = _rest[..count];
            _rest = _rest[count..];
            return true;
        }

        public bool TryReadU8(out int value)
        {
            bool ok = TryRead(1, out ReadOnlySpan<byte> b);
            value = ok ? b[0] : 0;
            return ok;
        }

        public bool TryReadU16(out int value)
        {
            bool ok = TryRead(2, out ReadOnlySpan<byte> b);
            value = ok ? (b[0] << 8) | b[1] : 0;
            return ok;
        }
    }
}
