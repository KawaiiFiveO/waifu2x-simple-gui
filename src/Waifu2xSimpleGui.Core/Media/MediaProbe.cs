using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Waifu2xSimpleGui.Core.Processes;

namespace Waifu2xSimpleGui.Core.Media;

/// <summary>Frame rate as ffmpeg's exact rational (e.g. <c>30000/1001</c>); pass <see cref="ToString"/> to ffmpeg.</summary>
public readonly record struct Rational(long Num, long Den)
{
    public double Value => Den == 0 ? 0 : (double)Num / Den;

    public bool IsValid => Num > 0 && Den > 0;

    public static Rational Parse(string? text)
    {
        var parts = (text ?? string.Empty).Split('/');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? new Rational(n, d)
            : default;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Num}/{Den}");
}

public sealed record StreamInfo(int Index, string CodecType, string? CodecName);

/// <summary>What the video pipeline needs to know about a source.</summary>
/// <param name="StreamIndex">Absolute index of the main video stream (cover art skipped).</param>
/// <param name="FrameRate">Output rate: avg_frame_rate, falling back to r_frame_rate.</param>
/// <param name="IsVfr">r_frame_rate ≠ avg_frame_rate; frames get normalized to <see cref="FrameRate"/>.</param>
/// <param name="VideoOffset">Video start relative to the container start (seconds); keeps A/V sync on mux.</param>
/// <param name="ColorMatrix">swscale matrix name: bt709, bt601 or bt2020 (guessed from height when untagged).</param>
public sealed record VideoInfo(
    int StreamIndex,
    int Width,
    int Height,
    Rational FrameRate,
    bool IsVfr,
    double Duration,
    double VideoOffset,
    long EstimatedFrames,
    string ColorMatrix,
    bool FullRange,
    string? ColorPrimaries,
    string? ColorTransfer,
    IReadOnlyList<StreamInfo> Streams)
{
    /// <summary>
    /// VFR passthrough: presentation time of every frame, seconds from the first frame, plus one trailing
    /// entry = end of the last frame. <c>null</c> = constant frame rate at <see cref="FrameRate"/>.
    /// </summary>
    public IReadOnlyList<double>? FrameTimes { get; init; }

    /// <summary>Duration of frames [from, to) — exact with <see cref="FrameTimes"/>, else from the rate.</summary>
    public double SpanSeconds(int from, int to) => FrameTimes is { } t
        ? t[Math.Min(to, t.Count - 1)] - t[Math.Min(from, t.Count - 1)]
        : (to - from) / FrameRate.Value;

    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>Value for ffmpeg's <c>-colorspace</c> output tag.</summary>
    public string ColorSpaceTag => ColorMatrix switch
    {
        "bt709" => "bt709",
        "bt2020" => "bt2020nc",
        _ => "smpte170m",
    };
}

public static class MediaProbe
{
    public static async Task<VideoInfo> ProbeVideoAsync(string ffprobePath, string path, CancellationToken cancellationToken)
    {
        var (result, json) = await ProcessRunner.CaptureAsync(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"ffprobe failed: {string.Join(' ', result.OutputTail)}");
        }

        return ParseVideo(json);
    }

    /// <summary>
    /// Frame timestamps for VFR passthrough, from packet PTS (demux only — no decode, so fast).
    /// Returns <see cref="VideoInfo.FrameTimes"/>-style values: relative to the first frame, plus the end time.
    /// </summary>
    public static async Task<IReadOnlyList<double>> ProbeFrameTimesAsync(string ffprobePath, string path, VideoInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        var (result, csv) = await ProcessRunner.CaptureAsync(
            ffprobePath,
            ["-v", "error", "-select_streams", info.StreamIndex.ToString(CultureInfo.InvariantCulture), "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", path],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"ffprobe failed: {string.Join(' ', result.OutputTail)}");
        }

        return ParseFrameTimes(csv);
    }

    internal static IReadOnlyList<double> ParseFrameTimes(string csv)
    {
        var packets = new List<(double Pts, double Duration)>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (ParseDouble(parts[0]) is { } pts)
            {
                packets.Add((pts, parts.Length > 1 ? ParseDouble(parts[1]) ?? 0 : 0));
            }
        }

        if (packets.Count == 0)
        {
            throw new InvalidDataException("No video timestamps.");
        }

        // Packets come in decode order; presentation order is sorted PTS (B-frames).
        packets.Sort((a, b) => a.Pts.CompareTo(b.Pts));
        var start = packets[0].Pts;
        var times = packets.Select(p => p.Pts - start).ToList();
        var lastDuration = packets[^1].Duration > 0
            ? packets[^1].Duration
            : times.Count > 1 ? times[^1] - times[^2] : 1.0 / 30;
        times.Add(times[^1] + lastDuration);
        return times;
    }

    internal static VideoInfo ParseVideo(string json)
    {
        var probe = JsonSerializer.Deserialize(json, ProbeJsonContext.Default.ProbeOutput)
            ?? throw new InvalidDataException("Empty ffprobe output.");
        var streams = probe.Streams ?? [];
        var v = streams.FirstOrDefault(s => s.CodecType == "video" && s.Disposition?.AttachedPic != 1)
            ?? throw new InvalidDataException("No video stream.");

        var avg = Rational.Parse(v.AvgFrameRate);
        var r = Rational.Parse(v.RFrameRate);
        var fps = avg.IsValid ? avg : r;
        if (!fps.IsValid)
        {
            throw new InvalidDataException("Unknown frame rate.");
        }

        var isVfr = avg.IsValid && r.IsValid && avg.Num * r.Den != r.Num * avg.Den;
        var duration = ParseDouble(v.Duration) ?? ParseDouble(probe.Format?.Duration) ?? 0;
        var frames = !isVfr && long.TryParse(v.NbFrames, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nb) && nb > 0
            ? nb
            : (long)Math.Round(duration * fps.Value);
        var offset = Math.Max(0, (ParseDouble(v.StartTime) ?? 0) - (ParseDouble(probe.Format?.StartTime) ?? 0));

        var matrix = v.ColorSpace switch
        {
            "bt709" => "bt709",
            "smpte170m" or "bt470bg" or "fcc" => "bt601",
            "bt2020nc" or "bt2020c" => "bt2020",
            _ => v.Height >= 720 ? "bt709" : "bt601",
        };

        return new VideoInfo(
            v.Index,
            v.Width,
            v.Height,
            fps,
            isVfr,
            duration,
            offset,
            Math.Max(1, frames),
            matrix,
            v.ColorRange == "pc",
            Known(v.ColorPrimaries),
            Known(v.ColorTransfer),
            [.. streams.Select(s => new StreamInfo(s.Index, s.CodecType ?? "", s.CodecName))]);
    }

    private static string? Known(string? s) => s is null or "unknown" ? null : s;

    private static double? ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

internal sealed class ProbeOutput
{
    public List<ProbeStream>? Streams { get; set; }

    public ProbeFormat? Format { get; set; }
}

internal sealed class ProbeFormat
{
    public string? Duration { get; set; }

    public string? StartTime { get; set; }
}

internal sealed class ProbeStream
{
    public int Index { get; set; }

    public string? CodecType { get; set; }

    public string? CodecName { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? RFrameRate { get; set; }

    public string? AvgFrameRate { get; set; }

    public string? NbFrames { get; set; }

    public string? Duration { get; set; }

    public string? StartTime { get; set; }

    public string? ColorSpace { get; set; }

    public string? ColorRange { get; set; }

    public string? ColorPrimaries { get; set; }

    public string? ColorTransfer { get; set; }

    public ProbeDisposition? Disposition { get; set; }
}

internal sealed class ProbeDisposition
{
    public int AttachedPic { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProbeOutput))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
