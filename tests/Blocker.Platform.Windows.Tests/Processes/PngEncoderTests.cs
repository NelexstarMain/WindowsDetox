using System.Buffers.Binary;
using System.IO.Compression;
using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

public sealed class PngEncoderTests
{
    private sealed record Chunk(string Type, byte[] Data, uint Crc);

    private static List<Chunk> ReadChunks(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(PngEncoder.Signature));
        var chunks = new List<Chunk>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            chunks.Add(new Chunk(type, data, crc));
            offset += 12 + length;
        }

        Assert.Equal(png.Length, offset);
        return chunks;
    }

    private static byte[] Inflate(byte[] zlibData)
    {
        using var input = new MemoryStream(zlibData);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void Crc32_MatchesKnownPngVectors()
    {
        // Every PNG ends with the same IEND chunk, CRC AE426082.
        Assert.Equal(0xAE426082u, PngEncoder.Crc32("IEND"u8, []));
        Assert.Equal(0xCBF43926u, PngEncoder.Crc32([], "123456789"u8)); // standard CRC-32 check value
    }

    [Fact]
    public void EncodeBgra_ProducesStructurallyValidPng()
    {
        const int Width = 3;
        const int Height = 2;
        var bgra = new byte[Width * Height * 4];
        for (var i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(i * 7);
        }

        var chunks = ReadChunks(PngEncoder.EncodeBgra(Width, Height, bgra));

        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(static c => c.Type));
        Assert.All(chunks, c => Assert.Equal(PngEncoder.Crc32(System.Text.Encoding.ASCII.GetBytes(c.Type), c.Data), c.Crc));

        var header = chunks[0].Data;
        Assert.Equal(13, header.Length);
        Assert.Equal((uint)Width, BinaryPrimitives.ReadUInt32BigEndian(header));
        Assert.Equal((uint)Height, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        Assert.Equal([8, 6, 0, 0, 0], header[8..]); // 8-bit RGBA, deflate, adaptive filter, no interlace
        Assert.Empty(chunks[2].Data);
    }

    [Fact]
    public void EncodeBgra_PixelsRoundTripAsRgba()
    {
        // Two pixels: opaque blue-ish and half transparent red.
        byte[] bgra = [200, 100, 50, 255, /* B G R A */ 0, 0, 255, 128];

        var raw = Inflate(ReadChunks(PngEncoder.EncodeBgra(2, 1, bgra))[1].Data);

        Assert.Equal([0, /* filter */ 50, 100, 200, 255, /* R G B A */ 255, 0, 0, 128], raw);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 2, 15)] // buffer size does not match
    public void EncodeBgra_RejectsInvalidArguments(int width, int height, int length) =>
        Assert.ThrowsAny<ArgumentException>(() => PngEncoder.EncodeBgra(width, height, new byte[length]));
}
