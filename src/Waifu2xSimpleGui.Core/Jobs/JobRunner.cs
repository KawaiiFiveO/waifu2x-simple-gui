using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Pipelines;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Jobs;

/// <summary>Runs one job: validates, then routes files to the pipeline for their media kind.</summary>
public static class JobRunner
{
    /// <summary>Returns a user-facing reason the job can't start, or <c>null</c>.</summary>
    public static string? Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Resolve(settings);
        var engine = CreateEngine(settings);
        if (settings.SizeMode != OutputSizeMode.ModelScale && new ToolLocator(settings.EnginesDirectory).FindFfmpeg() is null)
        {
            return "ffmpeg is required for the output size setting.";
        }

        if (settings.SizeMode == OutputSizeMode.Factor && settings.TargetFactor is <= 0 or > 32)
        {
            return "Output factor must be between 0 and 32.";
        }

        if (settings.SkipsUpscale)
        {
            return ValidateOutput(settings);
        }

        if (!engine.IsInstalled)
        {
            return $"{engine.Descriptor.DisplayName} not found at {engine.ExecutablePath}. See engines/README.md.";
        }

        if (!ThreadConfig.TryParse(settings.Threads, out _))
        {
            return $"Threads must look like load:proc:save (e.g. {ThreadConfig.Default}).";
        }

        return ValidateOutput(settings) ?? engine.Validate(settings.ToRequest("in", "out"));
    }

    /// <summary>Checks that apply with or without the upscaling engine.</summary>
    private static string? ValidateOutput(AppSettings settings)
    {
        if (VideoCommands.ContainerError(settings.VideoEncoder, settings.VideoContainer) is { } containerError)
        {
            return containerError;
        }

        if (settings.GifOutput == AnimationFormat.Mp4 && settings.VideoEncoder == VideoCommands.ProRes)
        {
            return "GIF → MP4 can't use ProRes — choose another video encoder or GIF/APNG output.";
        }

        if (settings.InterpolationFactor > 1)
        {
            var rife = RifeNcnnVulkan.Create(new ToolLocator(settings.EnginesDirectory).EnginesRoot);
            if (!rife.IsInstalled)
            {
                return $"RIFE not found at {rife.ExecutablePath} (needed for frame interpolation). See engines/README.md.";
            }

            if (RifeNcnnVulkan.Validate(settings) is { } rifeError)
            {
                return rifeError;
            }
        }

        return null;
    }

    /// <summary>Settings as the pipelines use them: Factor mode picks the engine scale.</summary>
    public static AppSettings Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { Scale = OutputSize.EngineScale(settings) };
    }

    public static IUpscaler CreateEngine(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UpscalerCatalog.Create(settings.Engine, new ToolLocator(settings.EnginesDirectory).EnginesRoot);
    }

    /// <param name="pause">Optional: lets the caller suspend/resume every process this job starts.</param>
    public static async Task RunAsync(IReadOnlyList<JobFile> files, AppSettings settings, IProgress<JobEvent> progress, CancellationToken cancellationToken, PauseController? pause = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        using var pauseScope = pause?.Enter();
        using var priorityScope = settings.LowPriority ? ProcessPriority.EnterLow() : null;
        settings = Resolve(settings);
        var tools = new ToolLocator(settings.EnginesDirectory);
        var engine = CreateEngine(settings);
        if (settings.SizeMode == OutputSizeMode.Factor && !settings.SkipsUpscale && settings.TargetFactor > settings.Scale)
        {
            progress.Report(new JobLog($"The model's largest scale is {settings.Scale}x; the rest of {settings.TargetFactor:0.##}x is plain lanczos upscaling."));
        }

        foreach (var f in files.Where(f => f.Kind == MediaKind.Unsupported))
        {
            progress.Report(new FileUpdate(f.Id, FileState.Skipped, "Unsupported file type."));
        }

        var (ffmpeg, ffprobe) = (tools.FindFfmpeg(), tools.FindFfprobe());
        var naming = new OutputNaming(settings);

        // GIFs are probed first: single-frame ones are just images.
        var animated = new List<(JobFile, GifInfo)>();
        var stills = new List<JobFile>();
        foreach (var gif in files.Where(f => f.Kind == MediaKind.AnimatedGif))
        {
            if (ffmpeg is null || ffprobe is null)
            {
                progress.Report(new FileUpdate(gif.Id, FileState.Failed, "ffmpeg/ffprobe not found."));
                continue;
            }

            try
            {
                var info = await GifCommands.ProbeAsync(ffprobe, gif.SourcePath, cancellationToken).ConfigureAwait(false);
                if (info.DelaysCs.Count > 1)
                {
                    animated.Add((gif, info));
                    progress.Report(new FileWeight(gif.Id, info.DelaysCs.Count));
                }
                else
                {
                    stills.Add(gif);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                progress.Report(new FileUpdate(gif.Id, FileState.Failed, ex.Message));
            }
        }

        var videos = files.Where(f => f.Kind == MediaKind.Video).ToList();
        if (ffprobe is not null)
        {
            await ReportVideoWeightsAsync(ffprobe, videos, progress, cancellationToken).ConfigureAwait(false);
        }

        var images = files.Where(f => f.Kind == MediaKind.Image).Concat(stills).ToList();
        await new ImagePipeline(engine, tools).RunAsync(images, settings, progress, cancellationToken).ConfigureAwait(false);

        if (animated.Count > 0)
        {
            await new GifPipeline(engine, ffmpeg!).RunAsync(animated, settings, naming, progress, cancellationToken).ConfigureAwait(false);
        }

        if (videos.Count == 0)
        {
            return;
        }

        var rife = settings.InterpolationFactor > 1 ? RifeNcnnVulkan.Create(tools.EnginesRoot) : null;
        var pipeline = ffmpeg is null || ffprobe is null ? null : new VideoPipeline(engine, ffmpeg, ffprobe, rife);
        foreach (var video in videos)
        {
            if (pipeline is null)
            {
                progress.Report(new FileUpdate(video.Id, FileState.Failed, "ffmpeg/ffprobe not found."));
                continue;
            }

            try
            {
                await pipeline.RunAsync(video, settings, naming, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                progress.Report(new FileUpdate(video.Id, FileState.Cancelled,
                    VideoPipeline.IsDirectResize(settings) ? null : "Encoded segments are kept; start again to resume."));
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EngineFailedException or System.Text.Json.JsonException)
            {
                // One bad video must not stop the rest of the batch.
                progress.Report(new FileUpdate(video.Id, FileState.Failed, WithDiskFullHint(ex.Message)));
            }
        }
    }

    /// <summary>
    /// Disk-full errors get a pointer to the fix. Video frames are large and Linux /tmp is often a small tmpfs
    /// (RAM, sometimes with per-user quotas: "Disk quota exceeded").
    /// </summary>
    internal static string WithDiskFullHint(string message) =>
        message.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase)
        || message.Contains("No space left", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
            ? message + " — the temp or output drive is full. Set Advanced → Temp folder to a larger drive, or use fewer frames per segment."
            : message;

    /// <summary>Frame counts up front so a long video isn't weighted like one image (fast: header probe only).</summary>
    private static async Task ReportVideoWeightsAsync(string ffprobe, List<JobFile> videos, IProgress<JobEvent> progress, CancellationToken cancellationToken)
    {
        foreach (var video in videos)
        {
            try
            {
                var info = await MediaProbe.ProbeVideoAsync(ffprobe, video.SourcePath, cancellationToken).ConfigureAwait(false);
                progress.Report(new FileWeight(video.Id, info.EstimatedFrames));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                // The pipeline reports the problem when it gets to this file.
            }
        }
    }
}
