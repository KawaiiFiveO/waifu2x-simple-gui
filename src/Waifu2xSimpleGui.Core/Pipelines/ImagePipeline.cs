using System.Globalization;
using System.Threading.Channels;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>
/// Still images: stage every input under an ASCII index name (hard link, or ffmpeg → PNG for
/// formats the engines can't decode), run the engine once on the folder, then move each output to
/// its final name as soon as the engine reports it.
/// </summary>
public sealed class ImagePipeline(IUpscaler engine, ToolLocator tools)
{
    public async Task RunAsync(IReadOnlyList<JobFile> files, AppSettings settings, IProgress<JobEvent> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        if (files.Count == 0)
        {
            return;
        }

        using var workspace = TempWorkspace.Create(settings.TempDirectory);
        var inDir = workspace.CreateSubdirectory("in");
        var outDir = workspace.CreateSubdirectory("out");
        var outExt = "." + settings.ImageFormat.ToString().ToLowerInvariant();
        var naming = new OutputNaming(settings);

        var entries = new Entry[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var stem = (i + 1).ToString("D6", CultureInfo.InvariantCulture);
            entries[i] = new Entry(files[i], stem, naming.Reserve(files[i].SourcePath, outExt), Path.Combine(outDir, stem + outExt));
        }

        await StageAsync(entries, inDir, progress, cancellationToken).ConfigureAwait(false);
        var byStem = entries.Where(e => !e.Finished).ToDictionary(e => e.Stem, StringComparer.OrdinalIgnoreCase);
        if (byStem.Count == 0)
        {
            return;
        }

        foreach (var e in byStem.Values)
        {
            progress.Report(new FileUpdate(e.File.Id, FileState.Processing));
        }

        // Moves (or resizes) run off the engine's output thread so a slow destination never stalls the engine's pipes.
        var resize = OutputSize.Filter(settings, settings.Scale, even: false);
        var ffmpeg = resize is null ? null : tools.FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg is required for the output size setting.");
        Task FinishAsync(Entry e) => resize is null
            ? Task.Run(() => MoveToFinal(e, settings.OverwriteExisting, progress), CancellationToken.None)
            : ResizeToFinalAsync(e, ffmpeg!, resize, settings.OverwriteExisting, progress, cancellationToken);

        var completed = Channel.CreateUnbounded<Entry>();
        var workers = resize is null ? 1 : Math.Max(1, Environment.ProcessorCount / 2);
        var mover = Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(
            async () =>
            {
                await foreach (var e in completed.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await FinishAsync(e).ConfigureAwait(false);
                }
            },
            CancellationToken.None)));

        void OnEngineEvent(EngineEvent ev)
        {
            switch (ev)
            {
                case EngineFileCompleted done when byStem.TryGetValue(StemOf(done.InputPath), out var e):
                    completed.Writer.TryWrite(e);
                    break;
                case EngineFileFailed failed when byStem.TryGetValue(StemOf(failed.InputPath), out var e):
                    e.Finished = true;
                    progress.Report(new FileUpdate(e.File.Id, FileState.Failed, $"Engine: {failed.Reason}"));
                    break;
                case EngineMessage msg when !string.IsNullOrWhiteSpace(msg.Text):
                    progress.Report(new JobLog(msg.Text));
                    break;
            }
        }

        string? engineError = null;
        var cancelled = false;
        try
        {
            await engine.RunAsync(settings.ToRequest(inDir, outDir), new InlineProgress<EngineEvent>(OnEngineEvent), cancellationToken).ConfigureAwait(false);
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
            completed.Writer.Complete();
            await mover.ConfigureAwait(false);
        }

        foreach (var e in byStem.Values.Where(e => !e.Finished))
        {
            if (!cancelled && File.Exists(e.TempOutput))
            {
                await FinishAsync(e).ConfigureAwait(false);
            }
            else
            {
                e.Finished = true;
                progress.Report(cancelled
                    ? new FileUpdate(e.File.Id, FileState.Cancelled)
                    : new FileUpdate(e.File.Id, FileState.Failed, engineError ?? "Engine produced no output."));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task StageAsync(Entry[] entries, string inDir, IProgress<JobEvent> progress, CancellationToken cancellationToken)
    {
        var ffmpeg = tools.FindFfmpeg();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        };

        await Parallel.ForEachAsync(entries, options, async (e, ct) =>
        {
            try
            {
                var source = e.File.SourcePath;
                if (MediaClassifier.IsEngineNativeImage(source))
                {
                    FileLinker.LinkOrCopy(source, Path.Combine(inDir, e.Stem + Path.GetExtension(source).ToLowerInvariant()));
                }
                else if (ffmpeg is null)
                {
                    Fail(e, "ffmpeg not found (needed for this format).");
                }
                else
                {
                    await Ffmpeg.ConvertToPngAsync(ffmpeg, source, Path.Combine(inDir, e.Stem + ".png"), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(e, ex.Message);
            }
        }).ConfigureAwait(false);

        void Fail(Entry e, string reason)
        {
            e.Finished = true;
            progress.Report(new FileUpdate(e.File.Id, FileState.Failed, reason));
        }
    }

    private static void MoveToFinal(Entry e, bool overwrite, IProgress<JobEvent> progress)
    {
        e.Finished = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(e.FinalPath)!);
            File.Move(e.TempOutput, e.FinalPath, overwrite);
            progress.Report(new FileUpdate(e.File.Id, FileState.Done, OutputPath: e.FinalPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress.Report(new FileUpdate(e.File.Id, FileState.Failed, $"Could not write output: {ex.Message}"));
        }
    }

    private static async Task ResizeToFinalAsync(Entry e, string ffmpeg, string filter, bool overwrite, IProgress<JobEvent> progress, CancellationToken ct)
    {
        var resized = e.TempOutput + ".resized" + Path.GetExtension(e.TempOutput);
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, OutputSize.ResizeImage(e.TempOutput, filter, resized), cancellationToken: ct).ConfigureAwait(false);
            if (r.ExitCode != 0)
            {
                throw new IOException($"ffmpeg resize failed: {string.Join(' ', r.OutputTail)}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(e.FinalPath)!);
            File.Move(resized, e.FinalPath, overwrite);
            e.Finished = true;
            progress.Report(new FileUpdate(e.File.Id, FileState.Done, OutputPath: e.FinalPath));
        }
        catch (OperationCanceledException)
        {
            // Left unfinished: the sweep reports it as cancelled.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Finished = true;
            progress.Report(new FileUpdate(e.File.Id, FileState.Failed, $"Could not write output: {ex.Message}"));
        }
    }

    // Engines print "dir/000001.png" in directory mode, whatever the OS separator.
    internal static string StemOf(string enginePath) =>
        Path.GetFileNameWithoutExtension(enginePath[(enginePath.LastIndexOfAny(['/', '\\']) + 1)..]);

    private sealed class Entry(JobFile file, string stem, string finalPath, string tempOutput)
    {
        public JobFile File { get; } = file;

        public string Stem { get; } = stem;

        public string FinalPath { get; } = finalPath;

        public string TempOutput { get; } = tempOutput;

        public volatile bool Finished;
    }
}

/// <summary>Invokes the handler on the reporting thread (unlike <see cref="Progress{T}"/>, which posts to a captured context).</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
