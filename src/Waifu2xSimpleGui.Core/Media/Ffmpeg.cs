using Waifu2xSimpleGui.Core.Processes;

namespace Waifu2xSimpleGui.Core.Media;

public static class Ffmpeg
{
    /// <summary>Decodes any ffmpeg-readable image to a fast-compressed PNG (first frame/page only).</summary>
    public static Task ConvertToPngAsync(string ffmpegPath, string source, string destination, CancellationToken cancellationToken) =>
        ExtractFrameAsync(ffmpegPath, source, 0, destination, cancellationToken);

    /// <summary>One frame at <paramref name="seconds"/> (0 = first) as PNG — previews of videos/animations.</summary>
    public static async Task ExtractFrameAsync(string ffmpegPath, string source, double seconds, string destination, CancellationToken cancellationToken)
    {
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y"];
        if (seconds > 0)
        {
            args.AddRange(["-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-i", source, "-frames:v", "1", "-compression_level", "1", destination]);
        var result = await ProcessRunner.RunAsync(ffmpegPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(destination))
        {
            throw new IOException($"ffmpeg could not convert the image: {string.Join(' ', result.OutputTail)}");
        }
    }
}
