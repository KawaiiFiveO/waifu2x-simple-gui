using System.Buffers.Binary;

namespace Waifu2xSimpleGui.Core.Media;

/// <summary>Splits ffmpeg's <c>image2pipe</c> PNG stream into files by walking the chunk headers (no decoding).</summary>
public static class PngStream
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Copies the next PNG from <paramref name="input"/> to <paramref name="path"/>. Returns false at a clean end
    /// of stream; throws <see cref="EndOfStreamException"/> if the stream stops mid-image.
    /// </summary>
    /// <param name="buffer">Scratch space, at least 8 bytes (bigger = fewer writes).</param>
    public static async Task<bool> CopyNextAsync(Stream input, string path, byte[] buffer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfLessThan(buffer.Length, 8);
        var read = await input.ReadAtLeastAsync(buffer.AsMemory(0, 8), 8, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        if (read < 8)
        {
            throw new EndOfStreamException("Truncated PNG signature.");
        }

        if (!buffer.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG stream.");
        }

        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(buffer.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                // Chunk: length (big-endian), type, data, CRC.
                await input.ReadExactlyAsync(buffer.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
                var remaining = BinaryPrimitives.ReadUInt32BigEndian(buffer) + 4L;
                var end = buffer.AsSpan(4, 4).SequenceEqual("IEND"u8);
                await file.WriteAsync(buffer.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
                while (remaining > 0)
                {
                    var n = (int)Math.Min(remaining, buffer.Length);
                    await input.ReadExactlyAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    remaining -= n;
                }

                if (end)
                {
                    return true;
                }
            }
        }
    }
}
