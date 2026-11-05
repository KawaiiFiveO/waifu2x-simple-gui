using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>
/// Animated GIFs: frames of every GIF go into one folder (prefix per GIF) for a single engine run;
/// each GIF is reassembled as soon as all its frames are upscaled, keeping per-frame delays,
/// transparency and loop count.
/// </summary>
public sealed class GifPipeline(IUpscaler engine, string ffmpegPath)
{
    private sealed class Gif(JobFile file, GifInfo info, string prefix, string finalPath)
    {
        public JobFile File { get; } = file;

        public GifInfo Info { get; } = info;

        public string Prefix { get; } = prefix;

        public string FinalPath { get; } = finalPath;

        public int Frames;

        public int Upscaled;

        public volatile bool Finished;
    }

    public async Task RunAsync(IReadOnlyList<(JobFile File, GifInfo Info)> gifs, AppSettings settings, OutputNaming naming, IProgress<JobEvent> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gifs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(progress);
        if (gifs.Count == 0)
        {
            return;
        }

        using var workspace = TempWorkspace.Create(settings.TempDirectory);
        var inDir = workspace.CreateSubdirectory("in");
        var outDir = workspace.CreateSubdirectory("out");
        var ext = GifCommands.Extension(settings.GifOutput);
        var all = gifs.Select((g, i) => new Gif(g.File, g.Info, (i + 1).ToString("D4", CultureInfo.InvariantCulture), naming.Reserve(g.File.SourcePath, ext))).ToList();

        void Fail(Gif g, string reason)
        {
            g.Finished = true;
            progress.Report(new FileUpdate(g.File.Id, FileState.Failed, reason));
        }

        await Parallel.ForEachAsync(
            all,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            async (g, ct) =>
            {
                progress.Report(new FileUpdate(g.File.Id, FileState.Processing));
                var r = await ProcessRunner.RunAsync(ffmpegPath, GifCommands.Extract(g.File.SourcePath, Path.Combine(inDir.Replace("%", "%%", StringComparison.Ordinal), g.Prefix + "_%06d.png")), cancellationToken: ct).ConfigureAwait(false);
                g.Frames = Directory.GetFiles(inDir, g.Prefix + "_*.png").Length;
                if (r.ExitCode != 0 || g.Frames == 0)
                {
                    Fail(g, $"ffmpeg could not decode the GIF: {string.Join(' ', r.OutputTail)}");
                }
            }).ConfigureAwait(false);

        var byPrefix = all.Where(g => !g.Finished).ToDictionary(g => g.Prefix);
        if (byPrefix.Count == 0)
        {
            return;
        }

        if (settings.SkipsUpscale)
        {
            // Resize only: reassemble from the extracted frames (the resize filter is part of assembly).
            await Parallel.ForEachAsync(
                byPrefix.Values,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) },
                async (g, _) => await AssembleAsync(g, inDir, settings, progress, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var ready = Channel.CreateUnbounded<Gif>(new UnboundedChannelOptions { SingleReader = true });
        var assembler = Task.Run(
            async () =>
            {
                await foreach (var g in ready.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await AssembleAsync(g, outDir, settings, progress, cancellationToken).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        var failures = new ConcurrentDictionary<string, string>();
        void OnEngineEvent(EngineEvent ev)
        {
            var path = ev switch
            {
                EngineFileCompleted c => c.InputPath,
                EngineFileFailed f => f.InputPath,
                _ => null,
            };
            var name = path is null ? null : ImagePipeline.StemOf(path);
            if (name is null || name.Length < 5 || !byPrefix.TryGetValue(name[..4], out var g))
            {
                return;
            }

            if (ev is EngineFileFailed failed)
            {
                failures.TryAdd(g.Prefix, $"Engine: {failed.Reason} on frame {name[5..]}.");
                return;
            }

            var done = Interlocked.Increment(ref g.Upscaled);
            progress.Report(new FileProgress(g.File.Id, 90.0 * done / g.Frames));
            if (done == g.Frames)
            {
                ready.Writer.TryWrite(g);
            }
        }

        string? engineError = null;
        var cancelled = false;
        try
        {
            var request = settings with { ImageFormat = ImageFormat.Png };
            await engine.RunAsync(request.ToRequest(inDir, outDir), new InlineProgress<EngineEvent>(OnEngineEvent), cancellationToken).ConfigureAwait(false);
        }
        catch (EngineFailedException ex)
        {
            engineError = $"{ex.Engine} failed (exit code {ex.ExitCode}).";
            progress.Report(new JobLog(ex.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            ready.Writer.Complete();
            await assembler.ConfigureAwait(false);
        }

        foreach (var g in byPrefix.Values.Where(g => !g.Finished))
        {
            if (cancelled)
            {
                g.Finished = true;
                progress.Report(new FileUpdate(g.File.Id, FileState.Cancelled));
            }
            else if (Directory.GetFiles(outDir, g.Prefix + "_*.png").Length == g.Frames)
            {
                await AssembleAsync(g, outDir, settings, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Fail(g, failures.GetValueOrDefault(g.Prefix) ?? engineError ?? "Engine produced no output.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task AssembleAsync(Gif g, string outDir, AppSettings settings, IProgress<JobEvent> progress, CancellationToken ct)
    {
        g.Finished = true;
        var temp = g.FinalPath + ".w2xsg-part";
        try
        {
            var frames = Directory.GetFiles(outDir, g.Prefix + "_*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            var delays = g.Info.DelaysCs;
            if (frames.Count != delays.Count)
            {
                progress.Report(new JobLog($"{Path.GetFileName(g.File.SourcePath)}: {frames.Count} frames but {delays.Count} delays; timing approximated."));
            }

            var format = settings.GifOutput;
            var list = Path.Combine(outDir, g.Prefix + ".txt");
            await File.WriteAllTextAsync(list, GifCommands.ConcatList(frames!, delays, repeatLast: format == AnimationFormat.Mp4), ct).ConfigureAwait(false);
            var lastDelay = delays[Math.Min(frames.Count, delays.Count) - 1];
            Directory.CreateDirectory(Path.GetDirectoryName(g.FinalPath)!);
            var resize = OutputSize.Filter(settings, settings.Scale, even: format == AnimationFormat.Mp4);
            switch (format)
            {
                case AnimationFormat.Apng:
                    await FfmpegAsync(GifCommands.Apng(list, g.Info.Loop, lastDelay, temp, resize), ct).ConfigureAwait(false);
                    break;
                case AnimationFormat.Mp4:
                    await FfmpegAsync(GifCommands.Mp4(list, settings, temp, resize), ct).ConfigureAwait(false);
                    break;
                default:
                    // Not "<prefix>_….png": that pattern is how frames are counted.
                    var palette = Path.Combine(outDir, g.Prefix + ".palette.png");
                    await FfmpegAsync(GifCommands.PaletteGen(list, palette, resize), ct).ConfigureAwait(false);
                    await FfmpegAsync(GifCommands.PaletteUse(list, palette, g.Info.Loop, lastDelay, temp, resize), ct).ConfigureAwait(false);
                    break;
            }
            File.Move(temp, g.FinalPath, overwrite: true);
            progress.Report(new FileUpdate(g.File.Id, FileState.Done, OutputPath: g.FinalPath));

            foreach (var f in frames)
            {
                File.Delete(Path.Combine(outDir, f!));
            }
        }
        catch (OperationCanceledException)
        {
            progress.Report(new FileUpdate(g.File.Id, FileState.Cancelled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress.Report(new FileUpdate(g.File.Id, FileState.Failed, ex.Message));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private async Task FfmpegAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(ffmpegPath, args, cancellationToken: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"ffmpeg GIF assembly failed: {string.Join(' ', result.OutputTail)}");
        }
    }
}
