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
public sealed class VideoPipeline(IUpscaler engine, string ffmpegPath, string ffprobePath, RifeNcnnVulkan? rife = null)
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

        var interpolateOnly = settings.InterpolationFactor > 1 && settings.InterpolateOnly;
        var finalPath = interpolateOnly
            ? naming.Reserve(file.SourcePath, VideoCommands.OutputExtension(file.SourcePath), "rife", 1)
            : naming.Reserve(file.SourcePath, VideoCommands.OutputExtension(file.SourcePath));
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
        await MuxAsync(file.SourcePath, info, resume, finalPath, progress, name, perSegment, cancellationToken).ConfigureAwait(false);
        resume.Delete();
        progress.Report(new FileUpdate(file.Id, FileState.Done, OutputPath: finalPath));
    }

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

        var upscaleFrames = !(interpolate && settings.InterpolateOnly);
        var toExtract = perSegment + (interpolate ? 1 : 0);
        var outInfo = info with { FrameRate = new Rational(info.FrameRate.Num * factor, info.FrameRate.Den) };
        var outHeight = upscaleFrames ? info.Height * settings.Scale : info.Height;
        var resize = OutputSize.Filter(settings, upscaleFrames ? settings.Scale : 1, even: true);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;
        var extracted = Channel.CreateBounded<Segment>(1);
        var upscaled = Channel.CreateBounded<Segment>(1);
        var interpolated = Channel.CreateBounded<Segment>(1);
        var request = settings with { ImageFormat = ImageFormat.Png };

        // Progress in "ticks": an upscaled frame is worth `factor`, an interpolated output frame 1.
        var ticksPerFrame = (upscaleFrames ? factor : 0) + (interpolate ? factor : 0);
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
            async () =>
            {
                for (var k = 0; ; k++)
                {
                    if (done.TryGetValue(k, out var frames))
                    {
                        if (frames < perSegment)
                        {
                            break;
                        }

                        continue;
                    }

                    var dir = workspace.CreateSubdirectory(string.Create(CultureInfo.InvariantCulture, $"seg{k:D5}"));
                    var inDir = Directory.CreateDirectory(Path.Combine(dir, "in")).FullName;
                    await FfmpegAsync(VideoCommands.ExtractSegment(file.SourcePath, info, k, perSegment, inDir, toExtract), "extract", ct).ConfigureAwait(false);
                    var count = Directory.GetFiles(inDir, "*.png").Length;
                    if (count == 0)
                    {
                        Directory.Delete(dir, recursive: true);
                        if (k == 0)
                        {
                            throw new InvalidDataException("No video frames could be decoded.");
                        }

                        break;
                    }

                    if (info.FrameTimes is { } times)
                    {
                        // Timestamps come from packets; the decoder must agree or timing would drift.
                        var expected = Math.Min(toExtract, times.Count - 1 - (k * perSegment));
                        if (count != expected)
                        {
                            throw new InvalidDataException(
                                $"Segment {k}: decoded {count} frames but the timestamps list {expected}. Turn off \"Keep variable frame rate\" for this file.");
                        }
                    }

                    await extracted.Writer.WriteAsync(new Segment(k, dir, Math.Min(count, perSegment), count, inDir), ct).ConfigureAwait(false);
                    if (count < toExtract)
                    {
                        break; // Last segment (no overlap frame available).
                    }
                }
            },
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

    private static void ExpectFrames(string dir, int expected, string who, int segment)
    {
        var produced = Directory.GetFiles(dir, "*.png").Length;
        if (produced != expected)
        {
            throw new IOException($"{who} produced {produced} of {expected} frames in segment {segment}.");
        }
    }

    private async Task MuxAsync(string source, VideoInfo info, VideoResume resume, string finalPath, IProgress<JobEvent> progress, string name, int perSegment, CancellationToken ct)
    {
        var list = Path.Combine(resume.DirectoryPath, "concat.txt");
        // VFR passthrough: explicit segment durations, so each segment starts exactly where its source frames do.
        await File.WriteAllLinesAsync(
            list,
            resume.Segments.Keys.Order().Select(k => $"file '{Path.GetFileName(resume.SegmentPath(k))}'"
                + (info.FrameTimes is null
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"\nduration {info.SpanSeconds(k * perSegment, (k * perSegment) + resume.Segments[k]):0.######}"))),
            ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var mkv = finalPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
        var temp = finalPath + ".w2xsg-part";
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpegPath, VideoCommands.Mux(list, source, info, temp, mkv, safeMode: false), cancellationToken: ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                progress.Report(new JobLog($"{name}: stream copy of audio/subtitles failed, retrying with AAC audio and no subtitles. {string.Join(' ', result.OutputTail)}"));
                await FfmpegAsync(VideoCommands.Mux(list, source, info, temp, mkv, safeMode: true), "mux", ct).ConfigureAwait(false);
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
