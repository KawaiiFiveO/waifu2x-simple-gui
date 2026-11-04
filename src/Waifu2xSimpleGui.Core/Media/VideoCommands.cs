using System.Globalization;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Media;

/// <summary>Output container for videos.</summary>
public enum VideoContainer
{
    /// <summary>mkv for mkv/webm sources, mov for ProRes, else mp4.</summary>
    Auto,
    Mp4,
    Mkv,

    /// <summary>For editors (After Effects, Premiere); no AV1.</summary>
    Mov,
}

/// <summary>ffmpeg argument lists for the video pipeline (pure; unit-tested).</summary>
public static class VideoCommands
{
    public const string FramePattern = "%08d.png";
    public const string ProRes = "prores_ks";

    public static IReadOnlyList<string> Encoders { get; } =
    [
        "libx264", "libx265", "libsvtav1",
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_amf", "hevc_amf", "av1_amf",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_videotoolbox", "hevc_videotoolbox",
        ProRes,
    ];

    public static bool IsAv1(string encoder) => encoder is "libsvtav1" || encoder.StartsWith("av1_", StringComparison.Ordinal);

    public static bool IsHevc(string encoder) => encoder is "libx265" || encoder.StartsWith("hevc_", StringComparison.Ordinal);

    /// <summary>Why <paramref name="encoder"/> can't go into <paramref name="container"/>, or <c>null</c>.</summary>
    public static string? ContainerError(string encoder, VideoContainer container) => (encoder, container) switch
    {
        (ProRes, VideoContainer.Mp4) => "ProRes can't be stored in MP4 — choose MOV, MKV or Auto.",
        _ when container == VideoContainer.Mov && IsAv1(encoder) => "AV1 can't be stored in MOV — choose MP4, MKV or Auto.",
        _ => null,
    };

    public static string PixelFormat(string encoder) =>
        encoder == ProRes ? "yuv422p10le" : encoder.EndsWith("_qsv", StringComparison.Ordinal) ? "nv12" : "yuv420p";

    /// <summary>Encoded segments: mp4 (mkv's 1 ms timestamps jitter odd rates), mov for ProRes (mp4 can't hold it).</summary>
    public static string SegmentFormat(string encoder) => encoder == ProRes ? "mov" : "mp4";

    public static IReadOnlyList<string> X26xPresets { get; } =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    /// <summary>prores_ks <c>-profile:v</c> names, smallest to largest.</summary>
    public static IReadOnlyList<string> ProResProfiles { get; } = ["proxy", "lt", "standard", "hq"];

    /// <summary>Preset choices for <paramref name="encoder"/>, or empty when it has none.</summary>
    public static IReadOnlyList<string> PresetsFor(string encoder) => encoder switch
    {
        "libx264" or "libx265" => X26xPresets,
        ProRes => ProResProfiles,
        _ => [],
    };

    public static string DefaultPreset(string encoder) => encoder == ProRes ? "hq" : "medium";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Every frame from <paramref name="startFrame"/> on, as a stream of RGB PNGs on stdout (<see cref="PngStream"/>
    /// splits it). One pass, no seeking: input seeks aren't frame-exact in MPEG-PS/TS, AVI, raw streams or Bink
    /// (measured), and they cost a keyframe decode per segment. Resume decodes the skipped frames but doesn't encode them.
    /// </summary>
    public static List<string> ExtractFrames(string source, VideoInfo info, long startFrame = 0)
    {
        ArgumentNullException.ThrowIfNull(info);
        var filters = new List<string>();
        if (info.Interlaced)
        {
            // One frame per frame (rate unchanged); only frames the decoder flags as interlaced are touched.
            filters.Add("bwdif=mode=send_frame:parity=auto:deint=interlaced");
        }

        if (info.IsVfr && info.FrameTimes is null)
        {
            filters.Add($"fps={info.FrameRate}"); // normalize to constant fps
        }

        if (startFrame > 0)
        {
            filters.Add(string.Create(Inv, $"select=gte(n\\,{startFrame})")); // n counts frames after the fps filter
        }

        var range = info.FullRange ? "pc" : "tv";
        filters.Add($"scale=in_color_matrix={info.ColorMatrix}:in_range={range},format=rgb24");
        return
        [
            "-hide_banner", "-nostdin", "-v", "error",
            "-i", source,
            "-map", "0:" + info.StreamIndex.ToString(Inv),
            "-vf", string.Join(',', filters),
            "-fps_mode", "passthrough",
            "-c:v", "png", "-compression_level", "1",
            "-f", "image2pipe", "pipe:1",
        ];
    }

    /// <summary>File name of local frame <paramref name="index"/> (0-based) in a segment folder: matches <see cref="FramePattern"/> from 1.</summary>
    public static string FrameName(int index) => string.Create(Inv, $"{index + 1:D8}.png");

    /// <param name="resize">Optional resize filter (<see cref="OutputSize.Filter"/>) applied before encoding.</param>
    /// <param name="timingList">VFR passthrough: ffconcat list with per-frame durations (<see cref="FrameTimingList"/>) instead of a fixed rate.</param>
    public static List<string> EncodeSegment(string framesDir, VideoInfo info, AppSettings settings, string output, string? resize = null, string? timingList = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(settings);
        var encoder = settings.VideoEncoder;
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
        args.AddRange(timingList is null
            ? ["-framerate", info.FrameRate.ToString(), "-i", PatternIn(framesDir)]
            : ["-f", "concat", "-safe", "0", "-i", timingList, "-fps_mode", "passthrough"]);
        // Anamorphic sources (DVD, DV) keep their pixel aspect; the frames from disk are square.
        var sar = info.IsAnamorphic ? $",setsar={info.SampleAspect}" : string.Empty;
        args.AddRange(new List<string>
        {
            // yuv420p needs even dimensions (3x of an odd size isn't).
            "-vf", (resize is null ? "" : resize + ",") + $"pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_color_matrix={info.ColorMatrix}:out_range=tv,format={PixelFormat(encoder)}{sar}",
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
            "-f", SegmentFormat(encoder), output,
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
        else if (encoder == ProRes)
        {
            // Quality is set by the profile; 4444 profiles aren't offered (frames are 4:2:2 at most).
            args.AddRange(["-profile:v", ProResProfiles.Contains(preset) ? preset : "hq", "-vendor", "apl0"]);
        }

        return args;
    }

    /// <summary>
    /// Joins encoded segments (stream copy) and takes audio/subtitles/chapters/metadata from the source.
    /// mp4/mov keep only text subtitles (as mov_text). <paramref name="safeMode"/> re-encodes audio to AAC and
    /// drops subtitles — the fallback when stream copy is rejected by the container.
    /// </summary>
    /// <param name="container">Resolved (<see cref="ResolveContainer"/>), not Auto.</param>
    public static List<string> Mux(string concatList, string source, VideoInfo info, string output, VideoContainer container, string encoder, bool safeMode)
    {
        ArgumentNullException.ThrowIfNull(info);
        var mkv = container == VideoContainer.Mkv;
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

        if (!mkv && IsHevc(encoder))
        {
            args.AddRange(["-tag:v", "hvc1"]); // QuickTime / After Effects / Apple devices need hvc1, not hev1
        }

        args.AddRange(container switch
        {
            VideoContainer.Mkv => ["-f", "matroska"],
            VideoContainer.Mov => ["-movflags", "+faststart", "-f", "mov"],
            _ => ["-movflags", "+faststart", "-f", "mp4"],
        });
        args.Add(output);
        return args;
    }

    /// <summary>
    /// One-line log text for the AAC fallback after a failed stream copy, naming the codec ffmpeg rejected
    /// (e.g. "binkaudio_dct can't be stored in MP4 — audio converted to AAC.").
    /// </summary>
    public static string CopyFallbackMessage(IEnumerable<string> ffmpegOutput, VideoContainer container, bool hasSubtitles)
    {
        ArgumentNullException.ThrowIfNull(ffmpegOutput);
        const string marker = "Could not find tag for codec ";
        var codec = ffmpegOutput
            .Select(l => l.IndexOf(marker, StringComparison.Ordinal) is var i and >= 0 ? l[(i + marker.Length)..].Split(' ')[0] : null)
            .FirstOrDefault(c => c is not null);
        var name = container.ToString().ToUpperInvariant();
        var reason = codec is null ? $"Audio/subtitles couldn't be copied into {name}" : $"{codec} can't be stored in {name}";
        return reason + " — audio converted to AAC" + (hasSubtitles ? ", subtitles dropped." : ".");
    }

    /// <summary>Auto: mkv/webm sources stay Matroska (subs, fonts), ProRes goes to mov, everything else mp4.</summary>
    public static VideoContainer ResolveContainer(string source, VideoContainer container, string encoder) => container switch
    {
        VideoContainer.Auto when Path.GetExtension(source).ToLowerInvariant() is ".mkv" or ".webm" => VideoContainer.Mkv,
        VideoContainer.Auto when encoder == ProRes => VideoContainer.Mov,
        VideoContainer.Auto => VideoContainer.Mp4,
        _ => container,
    };

    public static string OutputExtension(VideoContainer resolved) => resolved switch
    {
        VideoContainer.Mkv => ".mkv",
        VideoContainer.Mov => ".mov",
        _ => ".mp4",
    };

    // image2 treats '%' in the path as a pattern character.
    private static string PatternIn(string dir) => Path.Combine(dir.Replace("%", "%%", StringComparison.Ordinal), FramePattern);
}
