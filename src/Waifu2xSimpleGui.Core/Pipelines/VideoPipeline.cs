using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>
/// Video: segments of N frames flow through extract → upscale → interpolate (optional) → encode,
/// concurrent stages linked by bounded channels (GPU never waits for ffmpeg; temp holds only a few
/// segments). Encoded segments persist for resume; finally they're concatenated and muxed with the
/// source's audio, subtitles, chapters and metadata.
/// </summary>
public sealed partial class VideoPipeline(IUpscaler engine, string ffmpegPath, string ffprobePath, RifeNcnnVulkan? rife = null)
{
    /// <param name="Frames">The segment's own source frames.</param>
    /// <param name="Extracted">Frames on disk: own + 1 overlap frame (the next segment's first) when interpolating.</param>
    /// <param name="Current">Folder holding the frames for the next stage.</param>
    private sealed record Segment(int Index, string Directory, int Frames, int Extracted, string Current);

    public async Task RunAsync(JobFile file, AppSettings settings, OutputNaming naming, IProgress<JobEvent> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(progress);
        var name = Path.GetFileName(file.SourcePath);
        progress.Report(new FileUpdate(file.Id, FileState.Processing));

        var info = await MediaProbe.ProbeVideoAsync(ffprobePath, file.SourcePath, cancellationToken).ConfigureAwait(false);
        if (info.IsVfr && settings.VfrPassthrough)
        {
            var times = await MediaProbe.ProbeFrameTimesAsync(ffprobePath, file.SourcePath, info, cancellationToken).ConfigureAwait(false);
            info = info with { FrameTimes = times, EstimatedFrames = times.Count - 1 };
            progress.Report(new JobLog($"{name}: variable frame rate — keeping the original timing ({times.Count - 1} frames)."));
        }
        else if (info.IsVfr)
        {
            var outFps = info.FrameRate.Value * Math.Max(1, settings.InterpolationFactor);
            progress.Report(new JobLog($"{name}: variable frame rate — output is constant {outFps:0.###} fps."));
        }

        if (settings.InterpolationFactor > 1)
        {
            progress.Report(new JobLog(info.FrameTimes is null
                ? $"{name}: interpolating {info.FrameRate.Value:0.###} → {info.FrameRate.Value * settings.InterpolationFactor:0.###} fps."
                : $"{name}: interpolating {settings.InterpolationFactor}x (variable timing kept)."));
        }

        if (info.IsHdr)
        {
            progress.Report(new JobLog($"{name}: HDR source — output will be 8-bit SDR and look washed out."));
        }

        if (info.Interlaced)
        {
            progress.Report(new JobLog($"{name}: interlaced — deinterlacing (same frame rate)."));
        }

        var interpolateOnly = settings.InterpolationFactor > 1 && settings.InterpolateOnly;
        var container = VideoCommands.ResolveContainer(file.SourcePath, settings.VideoContainer, settings.VideoEncoder);
        var extension = VideoCommands.OutputExtension(container);
        var finalPath = interpolateOnly && !settings.SkipsUpscale
            ? naming.Reserve(file.SourcePath, extension, "rife", 1)
            : naming.Reserve(file.SourcePath, extension);
        if (IsDirectResize(settings))
        {
            await ResizeDirectAsync(file, info, settings, finalPath, container, progress, name, cancellationToken).ConfigureAwait(false);
            return;
        }

        var perSegment = Math.Max(30, settings.VideoSegmentFrames);
        var resume = VideoResume.Open(settings, file.SourcePath, perSegment);
        var done = new Dictionary<int, int>(resume.Segments);
        if (done.Count > 0)
        {
            progress.Report(new JobLog($"{name}: resuming, {done.Count} segment(s) already encoded."));
        }

        using (var workspace = TempWorkspace.Create(settings.TempDirectory))
        {
            await RunStagesAsync(file, info, settings, perSegment, resume, done, workspace, progress, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new FileProgress(file.Id, 97));
        // VFR passthrough: explicit segment durations, so each segment starts exactly where its source frames do.
        var list = Path.Combine(resume.DirectoryPath, "concat.txt");
        await File.WriteAllLinesAsync(
            list,
            resume.Segments.Keys.Order().Select(k => $"file '{Path.GetFileName(resume.SegmentPath(k))}'"
                + (info.FrameTimes is null
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"\nduration {info.SpanSeconds(k * perSegment, (k * perSegment) + resume.Segments[k]):0.######}"))),
            cancellationToken).ConfigureAwait(false);
        await MuxAsync(file.SourcePath, info, list, finalPath, container, settings.VideoEncoder, progress, name, cancellationToken).ConfigureAwait(false);
        resume.Delete();
        progress.Report(new FileUpdate(file.Id, FileState.Done, OutputPath: finalPath));
    }

    /// <summary>Resize only, no interpolation: one ffmpeg pass, no frames or segments (nothing to resume).</summary>
    internal static bool IsDirectResize(AppSettings settings) => settings.SkipsUpscale && settings.InterpolationFactor <= 1;

    private async Task ResizeDirectAsync(
        JobFile file, VideoInfo info, AppSettings settings, string finalPath, VideoContainer container,
        IProgress<JobEvent> progress, string name, CancellationToken ct)
    {
        using var workspace = TempWorkspace.Create(settings.TempDirectory);
        var dir = workspace.CreateSubdirectory("video");
        var video = Path.Combine(dir, "video." + VideoCommands.SegmentFormat(settings.VideoEncoder));
        var resize = OutputSize.Filter(settings, 1, even: true);
        var total = Math.Max(1, info.EstimatedFrames);
        var lastPercent = -1;
        void OnLine(string line)
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var frame))
            {
                var percent = (int)Math.Min(95, 95 * frame / total);
                if (Interlocked.Exchange(ref lastPercent, percent) != percent)
                {
                    progress.Report(new FileProgress(file.Id, percent));
                }
            }
        }

        var result = await ProcessRunner.RunAsync(ffmpegPath, VideoCommands.TranscodeVideo(file.SourcePath, info, settings, video, resize), OnLine, cancellationToken: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // -progress lines (key=value) share the tail with ffmpeg's error.
            var error = result.OutputTail.Where(l => !ProgressLine().IsMatch(l));
            throw new IOException($"ffmpeg resize failed (exit {result.ExitCode}): {string.Join(' ', error)}");
        }

        progress.Report(new FileProgress(file.Id, 97));
        var list = Path.Combine(dir, "concat.txt");
        await File.WriteAllTextAsync(list, $"file '{Path.GetFileName(video)}'\n", ct).ConfigureAwait(false);
        await MuxAsync(file.SourcePath, info, list, finalPath, container, settings.VideoEncoder, progress, name, ct).ConfigureAwait(false);
        progress.Report(new FileUpdate(file.Id, FileState.Done, OutputPath: finalPath));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\w+=\S*$")]
    private static partial System.Text.RegularExpressions.Regex ProgressLine();

    private async Task RunStagesAsync(
        JobFile file, VideoInfo info, AppSettings settings, int perSegment, VideoResume resume,
        Dictionary<int, int> done, TempWorkspace workspace, IProgress<JobEvent> progress, CancellationToken cancellationToken)
    {
        var factor = settings.InterpolationFactor > 1 ? settings.InterpolationFactor : 1;
        var interpolate = factor > 1;
        if (interpolate && rife is null)
        {
            throw new InvalidOperationException("RIFE is required for frame interpolation.");
        }

        var upscaleFrames = !settings.SkipsUpscale && !(interpolate && settings.InterpolateOnly);
        var outInfo = info with { FrameRate = new Rational(info.FrameRate.Num * factor, info.FrameRate.Den) };
        var outHeight = upscaleFrames ? info.Height * settings.Scale : info.Height;
        var resize = OutputSize.Filter(settings, upscaleFrames ? settings.Scale : 1, even: true);
        var gpuStages = upscaleFrames || interpolate;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;
        var extracted = Channel.CreateBounded<Segment>(1);
        var upscaled = Channel.CreateBounded<Segment>(1);
        var interpolated = Channel.CreateBounded<Segment>(1);
        var request = settings with { ImageFormat = ImageFormat.Png };

        // Progress in "ticks": an upscaled frame is worth `factor`, an interpolated output frame 1;
        // resize only: an encoded frame 1.
        var ticksPerFrame = gpuStages ? (upscaleFrames ? factor : 0) + (interpolate ? factor : 0) : 1;
        var totalTicks = Math.Max(1, info.EstimatedFrames * ticksPerFrame);
        long ticks = done.Values.Sum() * (long)ticksPerFrame;
        var lastPercent = -1;
        void Advance(int amount)
        {
            var percent = (int)Math.Min(95, 95 * Interlocked.Add(ref ticks, amount) / totalTicks);
            if (Interlocked.Exchange(ref lastPercent, percent) != percent)
            {
                progress.Report(new FileProgress(file.Id, percent));
            }
        }

        InlineProgress<EngineEvent> CountDone(int amount) => new(e =>
        {
            if (e is EngineFileCompleted)
            {
                Advance(amount);
            }
        });

        Task Stage(Func<Task> body, ChannelWriter<Segment>? output) => Task.Run(
            async () =>
            {
                try
                {
                    await body().ConfigureAwait(false);
                }
                catch
                {
                    // One failing stage stops the others (they'd otherwise block on the channels).
                    await linked.CancelAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    output?.TryComplete();
                }
            },
            CancellationToken.None);

        var extract = Stage(
            () => ExtractAsync(file.SourcePath, info, perSegment, interpolate, done, workspace, extracted.Writer, ct),
            extracted.Writer);

        var upscale = Stage(
            async () =>
            {
                await foreach (var seg in extracted.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var next = seg;
                    if (upscaleFrames)
                    {
                        var outDir = Directory.CreateDirectory(Path.Combine(seg.Directory, "up")).FullName;
                        await engine.RunAsync(request.ToRequest(seg.Current, outDir), CountDone(factor), ct).ConfigureAwait(false);
                        ExpectFrames(outDir, seg.Extracted, "Engine", seg.Index);
                        Directory.Delete(seg.Current, recursive: true);
                        next = seg with { Current = outDir };
                    }

                    await upscaled.Writer.WriteAsync(next, ct).ConfigureAwait(false);
                }
            },
            upscaled.Writer);

        var interpolateStage = Stage(
            async () =>
            {
                await foreach (var seg in upscaled.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var next = seg;
                    if (interpolate)
                    {
                        var outDir = Directory.CreateDirectory(Path.Combine(seg.Directory, "rife")).FullName;
                        // n = m·count puts input j at output m·j; the outputs past m·own belong to the next segment.
                        var args = rife!.BuildArguments(seg.Current, outDir, factor * seg.Extracted, outHeight, settings);
                        await rife.RunAsync(args, CountDone(1), ct).ConfigureAwait(false);
                        ExpectFrames(outDir, factor * seg.Extracted, "RIFE", seg.Index);
                        foreach (var extra in Directory.GetFiles(outDir, "*.png").Order(StringComparer.Ordinal).Skip(factor * seg.Frames))
                        {
                            File.Delete(extra);
                        }

                        Directory.Delete(seg.Current, recursive: true);
                        next = seg with { Current = outDir };
                    }

                    await interpolated.Writer.WriteAsync(next, ct).ConfigureAwait(false);
                }
            },
            interpolated.Writer);

        var encode = Stage(
            async () =>
            {
                await foreach (var seg in interpolated.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var part = resume.SegmentPath(seg.Index) + ".part";
                    string? timing = null;
                    if (info.FrameTimes is { } times)
                    {
                        var frames = Directory.GetFiles(seg.Current, "*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
                        var durations = VideoCommands.OutputDurations(times, seg.Index * perSegment, seg.Frames, factor);
                        timing = Path.Combine(seg.Current, "timing.txt");
                        await File.WriteAllTextAsync(timing, VideoCommands.FrameTimingList(frames!, durations), ct).ConfigureAwait(false);
                    }

                    await FfmpegAsync(VideoCommands.EncodeSegment(seg.Current, outInfo, settings, part, resize, timing), "encode", ct).ConfigureAwait(false);
                    resume.Commit(seg.Index, seg.Frames, part);
                    Directory.Delete(seg.Directory, recursive: true);
                    if (!gpuStages)
                    {
                        Advance(seg.Frames);
                    }
                }
            },
            null);

        Task[] stages = [extract, upscale, interpolateStage, encode];
        try
        {
            await Task.WhenAll(stages).ConfigureAwait(false);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Report the stage that actually failed, not the cancellations it caused.
            var cause = stages
                .Where(t => t.IsFaulted)
                .Select(t => t.Exception!.InnerException!)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (cause is not null)
            {
                ExceptionDispatchInfo.Throw(cause);
            }

            throw;
        }
    }

    /// <summary>
    /// One ffmpeg pass from the first unfinished segment to the end; frames are split into segment folders as they
    /// arrive. The bounded channel is the back-pressure: while a write waits, the pipe isn't read and ffmpeg blocks.
    /// Interpolation: each segment also gets the next segment's first frame (overlap), hard-linked.
    /// </summary>
    private async Task ExtractAsync(
        string source, VideoInfo info, int perSegment, bool overlap, Dictionary<int, int> done,
        TempWorkspace workspace, ChannelWriter<Segment> output, CancellationToken ct)
    {
        var first = 0;
        while (done.TryGetValue(first, out var frames))
        {
            if (frames < perSegment)
            {
                return; // The last segment is already encoded.
            }

            first++;
        }

        var toExtract = perSegment + (overlap ? 1 : 0);
        (int Index, string Dir, string InDir, int Count)? open = null;
        (int Index, string Dir, string InDir, int Count)? waitingForOverlap = null;
        var truncated = false;

        async Task HandOffAsync((int Index, string Dir, string InDir, int Count) seg)
        {
            if (done.ContainsKey(seg.Index))
            {
                Directory.Delete(seg.Dir, recursive: true); // Encoded in an earlier run.
                return;
            }

            if (info.FrameTimes is { } times)
            {
                // Timestamps come from packets; the decoder must agree or timing would drift.
                var expected = Math.Min(toExtract, times.Count - 1 - (seg.Index * perSegment));
                if (seg.Count != expected)
                {
                    throw new InvalidDataException(
                        $"Segment {seg.Index}: decoded {seg.Count} frames but the timestamps list {expected}. Turn off \"Keep variable frame rate\" for this file.");
                }
            }

            await output.WriteAsync(new Segment(seg.Index, seg.Dir, Math.Min(seg.Count, perSegment), seg.Count, seg.InDir), ct).ConfigureAwait(false);
        }

        async Task ReadAsync(Stream stdout, CancellationToken token)
        {
            var buffer = new byte[1 << 20];
            for (var frame = (long)first * perSegment; ; frame++)
            {
                var index = (int)(frame / perSegment);
                if (open is not { } current || current.Index != index)
                {
                    var dir = workspace.CreateSubdirectory(string.Create(CultureInfo.InvariantCulture, $"seg{index:D5}"));
                    open = (index, dir, Directory.CreateDirectory(Path.Combine(dir, "in")).FullName, 0);
                }

                var seg = open.Value;
                var path = Path.Combine(seg.InDir, VideoCommands.FrameName(seg.Count));
                try
                {
                    if (!await PngStream.CopyNextAsync(stdout, path, buffer, token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (EndOfStreamException)
                {
                    // ffmpeg died mid-frame; its exit code and message explain why.
                    File.Delete(path);
                    truncated = true;
                    return;
                }

                open = seg = seg with { Count = seg.Count + 1 };
                if (waitingForOverlap is { } previous)
                {
                    FileLinker.LinkOrCopy(path, Path.Combine(previous.InDir, VideoCommands.FrameName(perSegment)));
                    waitingForOverlap = null;
                    await HandOffAsync(previous with { Count = toExtract }).ConfigureAwait(false);
                }

                if (seg.Count == perSegment)
                {
                    open = null;
                    if (overlap)
                    {
                        waitingForOverlap = seg;
                    }
                    else
                    {
                        await HandOffAsync(seg).ConfigureAwait(false);
                    }
                }
            }

            // End of video: whatever is left is the last segment (no overlap frame exists).
            if (waitingForOverlap is { } last)
            {
                await HandOffAsync(last).ConfigureAwait(false);
            }
            else if (open is { Count: > 0 } partial)
            {
                await HandOffAsync(partial).ConfigureAwait(false);
            }
            else if (open is { } empty)
            {
                Directory.Delete(empty.Dir, recursive: true);
                if (empty.Index == 0)
                {
                    throw new InvalidDataException("No video frames could be decoded.");
                }
            }
        }

        var result = await ProcessRunner.RunStreamingAsync(
            ffmpegPath, VideoCommands.ExtractFrames(source, info, (long)first * perSegment), ReadAsync, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || truncated)
        {
            throw new IOException($"ffmpeg extract failed (exit {result.ExitCode}): {string.Join(' ', result.OutputTail)}");
        }
    }

    private static void ExpectFrames(string dir, int expected, string who, int segment)
    {
        var produced = Directory.GetFiles(dir, "*.png").Length;
        if (produced != expected)
        {
            throw new IOException($"{who} produced {produced} of {expected} frames in segment {segment}.");
        }
    }

    /// <param name="list">concat list of the encoded video-only file(s).</param>
    private async Task MuxAsync(
        string source, VideoInfo info, string list, string finalPath, VideoContainer container, string encoder,
        IProgress<JobEvent> progress, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var temp = finalPath + ".w2xsg-part";
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpegPath, VideoCommands.Mux(list, source, info, temp, container, encoder, safeMode: false), cancellationToken: ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var hasSubtitles = info.Streams.Any(s => s.CodecType == "subtitle");
                progress.Report(new JobLog($"{name}: {VideoCommands.CopyFallbackMessage(result.OutputTail, container, hasSubtitles)}"));
                await FfmpegAsync(VideoCommands.Mux(list, source, info, temp, container, encoder, safeMode: true), "mux", ct).ConfigureAwait(false);
            }

            File.Move(temp, finalPath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private async Task FfmpegAsync(IReadOnlyList<string> args, string step, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(ffmpegPath, args, cancellationToken: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"ffmpeg {step} failed (exit {result.ExitCode}): {string.Join(' ', result.OutputTail)}");
        }
    }
}
