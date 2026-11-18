using System.Buffers.Binary;
using Blocker.Core.Ipc;

namespace Blocker.Core.Tests.Ipc;

public sealed class FrameCodecTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static byte[] Header(uint length)
    {
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    [Fact]
    public async Task Write_then_read_round_trips_the_payload()
    {
        using var stream = new MemoryStream();
        byte[] payload = [1, 2, 3, 4, 5];

        await FrameCodec.WriteFrameAsync(stream, payload, 1024, Ct);
        stream.Position = 0;

        Assert.Equal(payload, await FrameCodec.ReadFrameAsync(stream, 1024, Ct));
        Assert.Null(await FrameCodec.ReadFrameAsync(stream, 1024, Ct));
    }

    [Fact]
    public async Task Write_uses_a_little_endian_length_prefix()
    {
        using var stream = new MemoryStream();

        await FrameCodec.WriteFrameAsync(stream, new byte[0x102], 4096, Ct);

        Assert.Equal([0x02, 0x01, 0x00, 0x00], stream.ToArray()[..4]);
        Assert.Equal(4 + 0x102, stream.Length);
    }

    [Fact]
    public async Task Payload_of_exactly_the_limit_is_accepted()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteFrameAsync(stream, new byte[64], 64, Ct);
        stream.Position = 0;

        var frame = await FrameCodec.ReadFrameAsync(stream, 64, Ct);

        Assert.Equal(64, frame?.Length);
    }

    [Fact]
    public async Task Oversize_frame_is_rejected_before_the_payload_is_read_or_allocated()
    {
        // Only the header exists: a reader that tried to allocate or read the announced 1 GiB would fail differently or run out of memory.
        using var stream = new MemoryStream(Header(1u << 30));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var ex = await Assert.ThrowsAsync<FrameTooLargeException>(async () => await FrameCodec.ReadFrameAsync(stream, 64 * 1024, Ct));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1L << 30, ex.Length);
        Assert.Equal(64 * 1024, ex.MaxFrameBytes);
        Assert.Equal(FrameCodec.HeaderSize, stream.Position);
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public async Task Frame_one_byte_over_the_limit_is_rejected()
    {
        using var stream = new MemoryStream([.. Header(65), .. new byte[65]]);

        await Assert.ThrowsAsync<FrameTooLargeException>(async () => await FrameCodec.ReadFrameAsync(stream, 64, Ct));
    }

    [Fact]
    public async Task Length_with_the_high_bit_set_is_rejected_not_treated_as_negative()
    {
        using var stream = new MemoryStream(Header(0xFFFFFFFF));

        await Assert.ThrowsAsync<FrameTooLargeException>(async () => await FrameCodec.ReadFrameAsync(stream, int.MaxValue, Ct));
    }

    [Fact]
    public async Task Zero_length_frame_is_invalid()
    {
        using var stream = new MemoryStream(Header(0));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadFrameAsync(stream, 64, Ct));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Truncated_header_is_an_error(int headerBytes)
    {
        using var stream = new MemoryStream(Header(5)[..headerBytes]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await FrameCodec.ReadFrameAsync(stream, 64, Ct));
    }

    [Fact]
    public async Task Truncated_payload_is_an_error()
    {
        using var stream = new MemoryStream([.. Header(10), 1, 2, 3]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await FrameCodec.ReadFrameAsync(stream, 64, Ct));
    }

    [Fact]
    public async Task Empty_stream_is_a_clean_end()
    {
        using var stream = new MemoryStream();

        Assert.Null(await FrameCodec.ReadFrameAsync(stream, 64, Ct));
    }

    [Fact]
    public async Task Writing_more_than_the_limit_writes_nothing()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<FrameTooLargeException>(async () => await FrameCodec.WriteFrameAsync(stream, new byte[65], 64, Ct));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Writing_an_empty_payload_is_refused()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(async () => await FrameCodec.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty, 64, Ct));
    }

    [Fact]
    public async Task Frames_split_across_many_small_reads_are_reassembled()
    {
        var payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        using var whole = new MemoryStream();
        await FrameCodec.WriteFrameAsync(whole, payload, 1024, Ct);
        using var trickle = new TrickleStream(whole.ToArray(), chunk: 3);

        Assert.Equal(payload, await FrameCodec.ReadFrameAsync(trickle, 1024, Ct));
    }

    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
