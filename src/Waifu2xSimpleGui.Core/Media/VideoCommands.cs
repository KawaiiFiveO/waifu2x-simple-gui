using System.Globalization;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Media;

/// <summary>ffmpeg argument lists for the video pipeline (pure; unit-tested).</summary>
public static class VideoCommands
{
    public const string FramePattern = "%08d.png";

    public static IReadOnlyList<string> Encoders { get; } =
    [
        "libx264", "libx265", "libsvtav1",
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_amf", "hevc_amf", "av1_amf",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_videotoolbox", "hevc_videotoolbox",
    ];

    public static IReadOnlyList<string> X26xPresets { get; } =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Frames [k·N, k·N+N) as RGB PNGs. Input seeking half a frame before the boundary is frame-exact
    /// (verified against a full decode) and avoids decoding from the start for every segment.
    /// </summary>
    /// <param name="frameCount">Frames to write; defaults to <paramref name="framesPerSegment"/> (+1 = overlap frame for interpolation).</param>
    public static List<string> ExtractSegment(string source, VideoInfo info, int segment, int framesPerSegment, string outDir, int frameCount = 0)
    {
        ArgumentNullException.ThrowIfNull(info);
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
        if (segment > 0)
        {
            var first = segment * framesPerSegment;
            // CFR: half a frame before the boundary. VFR passthrough: midway between the real timestamps.
            var start = info.VideoOffset + (info.FrameTimes is { } t
                ? (t[Math.Min(first - 1, t.Count - 1)] + t[Math.Min(first, t.Count - 1)]) / 2
                : (first - 0.5) / info.FrameRate.Value);
            args.AddRange(["-ss", start.ToString("0.######", Inv)]);
        }

        var range = info.FullRange ? "pc" : "tv";
        var filter = $"scale=in_color_matrix={info.ColorMatrix}:in_range={range},format=rgb24";
        if (info.IsVfr && info.FrameTimes is null)
        {
            filter = $"fps={info.FrameRate},{filter}"; // normalize to constant fps
        }

        args.AddRange(
        [
            "-i", source,
            "-map", "0:" + info.StreamIndex.ToString(Inv),
            "-frames:v", (frameCount > 0 ? frameCount : framesPerSegment).ToString(Inv),
            "-vf", filter,
            "-fps_mode", "passthrough",
            "-compression_level", "1",
            PatternIn(outDir),
        ]);
        return args;
    }

    /// <param name="resize">Optional resize filter (<see cref="OutputSize.Filter"/>) applied before encoding.</param>
    /// <param name="timingList">VFR passthrough: ffconcat list with per-frame durations (<see cref="FrameTimingList"/>) instead of a fixed rate.</param>
    public static List<string> EncodeSegment(string framesDir, VideoInfo info, AppSettings settings, string output, string? resize = null, string? timingList = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(settings);
        var encoder = settings.VideoEncoder;
        var pixFmt = encoder.EndsWith("_qsv", StringComparison.Ordinal) ? "nv12" : "yuv420p";
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
        args.AddRange(timingList is null
            ? ["-framerate", info.FrameRate.ToString(), "-i", PatternIn(framesDir)]
            : ["-f", "concat", "-safe", "0", "-i", timingList, "-fps_mode", "passthrough"]);
        args.AddRange(new List<string>
        {
            // yuv420p needs even dimensions (3x of an odd size isn't).
            "-vf", (resize is null ? "" : resize + ",") + $"pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_color_matrix={info.ColorMatrix}:out_range=tv,format={pixFmt}",
        });
        args.AddRange(EncoderArguments(encoder, settings.VideoCrf, settings.VideoPreset));
        var (primaries, transfer) = info.ColorMatrix switch
        {
            "bt709" => ("bt709", "bt709"),
            "bt2020" => ("bt2020", "bt2020-10"),
            _ => ("smpte170m", "smpte170m"),
        };
        args.AddRange(
        [
            "-colorspace", info.ColorSpaceTag,
            "-color_primaries", info.ColorPrimaries ?? primaries,
            "-color_trc", info.ColorTransfer ?? transfer,
            "-color_range", "tv",
            // mp4, not mkv: Matroska's 1 ms timestamps make odd rates (e.g. 3267/125) jitter after concat.
            "-f", "mp4", output,
        ]);
        return args;
    }

    /// <summary>
    /// VFR passthrough: ffconcat list giving each frame its own duration on a 1/90000 timebase (the image
    /// default, 1/25, would quantize the timestamps — same issue as GIF delays).
    /// </summary>
    public static string FrameTimingList(IReadOnlyList<string> frameNames, IReadOnlyList<double> durations)
    {
        ArgumentNullException.ThrowIfNull(frameNames);
        ArgumentNullException.ThrowIfNull(durations);
        var sb = new System.Text.StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < frameNames.Count; i++)
        {
            sb.Append(Inv, $"file '{frameNames[i]}'\noption framerate 90000\nduration {durations[Math.Min(i, durations.Count - 1)]:0.######}\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Per-output-frame durations for frames [first, first + count) when each source interval is split into
    /// <paramref name="factor"/> interpolated frames (factor 1 = the source durations).
    /// </summary>
    public static List<double> OutputDurations(IReadOnlyList<double> frameTimes, int first, int count, int factor)
    {
        ArgumentNullException.ThrowIfNull(frameTimes);
        var result = new List<double>(count * factor);
        for (var i = first; i < first + count; i++)
        {
            var d = frameTimes[Math.Min(i + 1, frameTimes.Count - 1)] - frameTimes[Math.Min(i, frameTimes.Count - 1)];
            for (var j = 0; j < factor; j++)
            {
                result.Add(d / factor);
            }
        }

        return result;
    }

    /// <summary>Quality knob per encoder family: <paramref name="quality"/> is CRF-like (lower = better).</summary>
    public static List<string> EncoderArguments(string encoder, int quality, string preset)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoder);
        var q = quality.ToString(Inv);
        var args = new List<string> { "-c:v", encoder };
        if (encoder is "libx264" or "libx265")
        {
            args.AddRange(["-crf", q, "-preset", X26xPresets.Contains(preset) ? preset : "medium"]);
            if (encoder == "libx265")
            {
                args.AddRange(["-x265-params", "log-level=error"]);
            }
        }
        else if (encoder == "libsvtav1")
        {
            args.AddRange(["-crf", q, "-preset", int.TryParse(preset, NumberStyles.Integer, Inv, out var p) ? p.ToString(Inv) : "8"]);
        }
        else if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-rc", "vbr", "-cq", q, "-b:v", "0", "-preset", "p5"]);
        }
        else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            args.AddRange(["-rc", "cqp", "-qp_i", q, "-qp_p", q]);
        }
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
        {
            args.AddRange(["-global_quality", q]);
        }
        else if (encoder.EndsWith("_videotoolbox", StringComparison.Ordinal))
        {
            args.AddRange(["-q:v", Math.Clamp(100 - (quality * 2), 1, 100).ToString(Inv)]);
        }

        return args;
    }

    /// <summary>
    /// Joins encoded segments (stream copy) and takes audio/subtitles/chapters/metadata from the source.
    /// mp4 keeps only text subtitles (as mov_text). <paramref name="safeMode"/> re-encodes audio to AAC and
    /// drops subtitles — the fallback when stream copy is rejected by the container.
    /// </summary>
    public static List<string> Mux(string concatList, string source, VideoInfo info, string output, bool mkv, bool safeMode)
    {
        ArgumentNullException.ThrowIfNull(info);
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
        if (info.VideoOffset > 0.0005)
        {
            args.AddRange(["-itsoffset", info.VideoOffset.ToString("0.######", Inv)]);
        }

        args.AddRange(["-f", "concat", "-safe", "0", "-i", concatList, "-i", source, "-map", "0:v:0", "-map", "1:a?"]);
        if (!safeMode)
        {
            if (mkv)
            {
                args.AddRange(["-map", "1:s?", "-map", "1:t?"]);
            }
            else
            {
                foreach (var s in info.Streams.Where(s => s.CodecType == "subtitle" && s.CodecName is "subrip" or "ass" or "ssa" or "mov_text" or "webvtt" or "text"))
                {
                    args.AddRange(["-map", "1:" + s.Index.ToString(Inv)]);
                }
            }
        }

        args.AddRange(["-map_metadata", "1", "-map_chapters", "1", "-c", "copy"]);
        if (safeMode)
        {
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }
        else if (!mkv)
        {
            args.AddRange(["-c:s", "mov_text"]);
        }

        args.AddRange(mkv ? ["-f", "matroska"] : ["-movflags", "+faststart", "-f", "mp4"]);
        args.Add(output);
        return args;
    }

    /// <summary>mkv/webm sources stay Matroska (subs, fonts); everything else becomes mp4.</summary>
    public static string OutputExtension(string source) =>
        Path.GetExtension(source).ToLowerInvariant() is ".mkv" or ".webm" ? ".mkv" : ".mp4";

    // image2 treats '%' in the path as a pattern character.
    private static string PatternIn(string dir) => Path.Combine(dir.Replace("%", "%%", StringComparison.Ordinal), FramePattern);
}
