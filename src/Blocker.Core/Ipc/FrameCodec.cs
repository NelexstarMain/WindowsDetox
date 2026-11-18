using System.Buffers.Binary;

namespace Blocker.Core.Ipc;

/// <summary>A frame announced a length above the limit; raised before any payload memory is allocated.</summary>
public sealed class FrameTooLargeException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="length">Announced payload length.</param>
    /// <param name="maxFrameBytes">Enforced limit.</param>
    public FrameTooLargeException(long length, int maxFrameBytes)
        : base($"Frame of {length} bytes exceeds the limit of {maxFrameBytes} bytes.")
    {
        Length = length;
        MaxFrameBytes = maxFrameBytes;
    }

    /// <summary>Gets the announced payload length.</summary>
    public long Length { get; }

    /// <summary>Gets the enforced limit.</summary>
    public int MaxFrameBytes { get; }
}

/// <summary>Length-prefixed framing: a 4-byte little-endian payload length followed by the payload (UTF-8 JSON).</summary>
public static class FrameCodec
{
    /// <summary>Size of the length prefix.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Reads one frame. The announced length is validated before the payload buffer is allocated, so a hostile peer cannot make
    /// the reader allocate more than <paramref name="maxFrameBytes"/>.
    /// </summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="maxFrameBytes">Largest accepted payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The payload, or <see langword="null"/> when the stream ended cleanly between frames.</returns>
    /// <exception cref="FrameTooLargeException">The announced length exceeds <paramref name="maxFrameBytes"/>.</exception>
    /// <exception cref="InvalidDataException">The announced length is zero.</exception>
    /// <exception cref="EndOfStreamException">The stream ended inside a frame.</exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);

        var header = new byte[HeaderSize];
        var read = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new EndOfStreamException("The stream ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
        {
            throw new InvalidDataException("Empty frames are not allowed.");
        }

        if (length > (uint)maxFrameBytes)
        {
            throw new FrameTooLargeException(length, maxFrameBytes);
        }

        var payload = new byte[length];
        if (await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false) < payload.Length)
        {
            throw new EndOfStreamException("The stream ended inside a frame payload.");
        }

        return payload;
    }

    /// <summary>Writes one frame (header and payload in a single write).</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="payload">Payload; must not be empty.</param>
    /// <param name="maxFrameBytes">Largest allowed payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the frame is written and flushed.</returns>
    /// <exception cref="FrameTooLargeException"><paramref name="payload"/> exceeds <paramref name="maxFrameBytes"/>.</exception>
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, int maxFrameBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.IsEmpty)
        {
            throw new ArgumentException("Empty frames are not allowed.", nameof(payload));
        }

        if (payload.Length > maxFrameBytes)
        {
            throw new FrameTooLargeException(payload.Length, maxFrameBytes);
        }

        var buffer = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
        payload.CopyTo(buffer.AsMemory(HeaderSize));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
