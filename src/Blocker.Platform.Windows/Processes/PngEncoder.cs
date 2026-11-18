using System.Buffers.Binary;
using System.IO.Compression;

namespace Blocker.Platform.Windows.Processes;

/// <summary>Minimal PNG writer (8-bit RGBA, no interlace, filter 0). Pure managed code; System.Drawing is not available.</summary>
internal static class PngEncoder
{
    /// <summary>The 8-byte PNG file signature.</summary>
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes <paramref name="bgra"/> (top-down rows, 4 bytes per pixel, blue first, straight alpha) as a PNG.</summary>
    public static byte[] EncodeBgra(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = checked(width * 4);
        if (bgra.Length != checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer size does not match width x height x 4.", nameof(bgra));
        }

        // Each scanline: filter byte 0 followed by RGBA.
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * (stride + 1));
            row[0] = 0;
            var source = bgra.Slice(y * stride, stride);
            for (var x = 0; x < stride; x += 4)
            {
                row[1 + x] = source[x + 2];
                row[2 + x] = source[x + 1];
                row[3 + x] = source[x];
                row[4 + x] = source[x + 3];
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // color type: RGBA
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, Deflate(raw));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>CRC-32 (ISO 3309) as used by PNG chunks, over type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(type, data));
        output.Write(number);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
