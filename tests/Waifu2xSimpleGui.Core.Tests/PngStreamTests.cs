using Waifu2xSimpleGui.Core.Media;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class PngStreamTests
{
    // Signature + chunks; the splitter only walks lengths/types (CRCs aren't checked).
    private static byte[] FakePng(int dataLength, byte fill)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        void Chunk(string type, int length)
        {
            bytes.AddRange([(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            bytes.AddRange(Enumerable.Repeat(fill, length));
            bytes.AddRange([1, 2, 3, 4]); // CRC
        }

        Chunk("IHDR", 13);
        Chunk("IDAT", dataLength);
        Chunk("IEND", 0);
        return [.. bytes];
    }

    [Fact]
    public async Task Splits_concatenated_pngs_exactly_and_stops_at_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = FakePng(100_000, 7); // larger than the buffer: chunk data spans reads
        var b = FakePng(10, 9);
        using var input = new MemoryStream([.. a, .. b]);
        var dir = Directory.CreateTempSubdirectory("w2xsg-png").FullName;
        try
        {
            var buffer = new byte[4096];
            Assert.True(await PngStream.CopyNextAsync(input, Path.Combine(dir, "a.png"), buffer, ct));
            Assert.True(await PngStream.CopyNextAsync(input, Path.Combine(dir, "b.png"), buffer, ct));
            Assert.False(await PngStream.CopyNextAsync(input, Path.Combine(dir, "c.png"), buffer, ct));
            Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(dir, "a.png"), ct));
            Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(dir, "b.png"), ct));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Truncated_stream_throws_end_of_stream()
    {
        var ct = TestContext.Current.CancellationToken;
        var png = FakePng(1000, 1);
        using var input = new MemoryStream(png[..500]);
        var path = Path.Combine(Path.GetTempPath(), $"w2xsg-trunc-{Guid.NewGuid():N}.png");
        try
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => PngStream.CopyNextAsync(input, path, new byte[256], ct));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
