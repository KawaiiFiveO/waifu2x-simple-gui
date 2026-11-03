using System.Globalization;
using System.Text;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Media;

/// <summary>Output container for animated GIF sources.</summary>
public enum AnimationFormat
{
    Gif,

    /// <summary>Animated PNG (<c>.png</c>): full color + alpha, exact timing.</summary>
    Apng,

    /// <summary>Video using the video encoder settings; transparency is flattened onto black.</summary>
    Mp4,
}

/// <summary>Per-frame delays (centiseconds) and loop count (-1 = play once, 0 = forever, N = N repeats).</summary>
public sealed record GifInfo(IReadOnlyList<int> DelaysCs, int Loop);

/// <summary>GIF probing and ffmpeg argument lists (pure parts are unit-tested).</summary>
public static class GifCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<GifInfo> ProbeAsync(string ffprobePath, string path, CancellationToken cancellationToken)
    {
        // Packet durations are the frame delays; the demuxer already maps tiny delays to 10 cs like browsers do.
        var (result, stdout) = await ProcessRunner.CaptureAsync(
            ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=duration_time", "-of", "csv=p=0", path],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"ffprobe failed: {string.Join(' ', result.OutputTail)}");
        }

        var delays = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s.TrimEnd(','), NumberStyles.Float, Inv, out var d) ? Math.Max(1, (int)Math.Round(d * 100)) : 10)
            .ToList();
        if (delays.Count == 0)
        {
            throw new InvalidDataException("No GIF frames.");
        }

        var head = new byte[64 * 1024];
        int read;
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }

        return new GifInfo(delays, ParseLoop(head.AsSpan(0, read)));
    }

    /// <summary>NETSCAPE2.0 / ANIMEXTS1.0 application extension: <c>03 01 lo hi</c> after the identifier.</summary>
    public static int ParseLoop(ReadOnlySpan<byte> gif)
    {
        foreach (var id in new[] { "NETSCAPE2.0"u8.ToArray(), "ANIMEXTS1.0"u8.ToArray() })
        {
            var i = gif.IndexOf(id);
            if (i >= 0 && i + id.Length + 4 <= gif.Length && gif[i + id.Length] == 3 && gif[i + id.Length + 1] == 1)
            {
                return gif[i + id.Length + 2] | (gif[i + id.Length + 3] << 8);
            }
        }

        return -1;
    }

    public static List<string> Extract(string source, string outPattern) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", source, "-map", "0:v:0",
        "-fps_mode", "passthrough", "-pix_fmt", "rgba", "-compression_level", "1", outPattern,
    ];

    /// <summary>
    /// ffconcat list giving each frame its own delay. <c>option framerate 100</c> puts the images on a
    /// 1/100 s timebase; the default (1/25) would quantize 9 cs delays to 8/12.
    /// </summary>
    /// <param name="repeatLast">Re-list the last frame for 1 cs so containers without a "final delay" (mp4) keep its full duration.</param>
    public static string ConcatList(IReadOnlyList<string> frameNames, IReadOnlyList<int> delaysCs, bool repeatLast = false)
    {
        ArgumentNullException.ThrowIfNull(frameNames);
        ArgumentNullException.ThrowIfNull(delaysCs);
        var sb = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < frameNames.Count; i++)
        {
            var delay = delaysCs[Math.Min(i, delaysCs.Count - 1)];
            sb.Append(Inv, $"file '{frameNames[i]}'\noption framerate 100\nduration {delay / 100.0:0.00}\n");
        }

        if (repeatLast && frameNames.Count > 0)
        {
            sb.Append(Inv, $"file '{frameNames[^1]}'\noption framerate 100\nduration 0.01\n");
        }

        return sb.ToString();
    }

    /// <summary>Pass 1: one palette for the whole animation (frames are read from disk, not buffered in RAM).</summary>
    /// <param name="resize">Optional resize filter (<see cref="OutputSize.Filter"/>); also used by the other builders here.</param>
    public static List<string> PaletteGen(string concatList, string palette, string? resize = null) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", concatList,
        "-vf", Prefix(resize) + "palettegen=stats_mode=diff:reserve_transparent=1", "-frames:v", "1", "-update", "1", palette,
    ];

    /// <summary>Pass 2: map frames to the palette, keep timestamps, loop count and the last frame's delay.</summary>
    public static List<string> PaletteUse(string concatList, string palette, int loop, int finalDelayCs, string output, string? resize = null) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", concatList, "-i", palette,
        "-lavfi", $"[0:v]{resize ?? "null"}[f];[f][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle:alpha_threshold=128",
        "-fps_mode", "passthrough",
        "-loop", loop.ToString(Inv),
        "-final_delay", finalDelayCs.ToString(Inv),
        "-f", "gif", output,
    ];

    public static string Extension(AnimationFormat format) => format switch
    {
        AnimationFormat.Apng => ".png",
        AnimationFormat.Mp4 => ".mp4",
        _ => ".gif",
    };

    /// <summary>APNG keeps RGBA; <c>-plays</c> counts total plays (GIF loop N = N repeats → N+1).</summary>
    public static List<string> Apng(string concatList, int gifLoop, int finalDelayCs, string output, string? resize = null) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", concatList,
        "-vf", resize ?? "null",
        "-fps_mode", "passthrough", "-c:v", "apng", "-pix_fmt", "rgba",
        "-plays", (gifLoop < 0 ? 1 : gifLoop == 0 ? 0 : gifLoop + 1).ToString(Inv),
        "-final_delay", finalDelayCs.ToString(Inv) + "/100",
        "-f", "apng", output,
    ];

    /// <summary>Variable-frame-rate mp4 with the video encoder settings; alpha is premultiplied (→ black).</summary>
    public static List<string> Mp4(string concatList, AppSettings settings, string output, string? resize = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pixFmt = settings.VideoEncoder.EndsWith("_qsv", StringComparison.Ordinal) ? "nv12" : "yuv420p";
        List<string> args =
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", concatList,
            "-vf", $"{Prefix(resize)}premultiply=inplace=1,pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_color_matrix=bt709:out_range=tv,format={pixFmt}",
            "-fps_mode", "passthrough",
        ];
        args.AddRange(VideoCommands.EncoderArguments(settings.VideoEncoder, settings.VideoCrf, settings.VideoPreset));
        args.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv", "-movflags", "+faststart", "-f", "mp4", output]);
        return args;
    }

    private static string Prefix(string? filter) => filter is null ? string.Empty : filter + ",";
}
