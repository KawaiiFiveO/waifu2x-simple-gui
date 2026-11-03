using System.Collections.Concurrent;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Pipelines;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class ImagePipelineTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("w2xsg-pipe-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Write(string relative, string content = "img")
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private AppSettings Settings(string? outputDir = null) => new()
    {
        OutputDirectory = outputDir,
        TempDirectory = Path.Combine(_dir.FullName, "tmp"),
    };

    private string TempRoot => TempWorkspace.GetRoot(Path.Combine(_dir.FullName, "tmp"));

    [Fact]
    public async Task Moves_outputs_dedupes_names_and_reports_engine_failures()
    {
        var files = new List<JobFile>
        {
            new(10, Write("a/pic ü.png"), MediaKind.Image),
            new(11, Write("b/pic ü.png"), MediaKind.Image),
            new(12, Write("c/broken.jpg", "BAD"), MediaKind.Image),
        };
        var outDir = Path.Combine(_dir.FullName, "out");
        var updates = new Updates();

        await new ImagePipeline(new FakeEngine(), new ToolLocator(_dir.FullName))
            .RunAsync(files, Settings(outDir), updates, TestContext.Current.CancellationToken);

        Assert.Equal(FileState.Done, updates.Final(10).State);
        Assert.Equal(FileState.Done, updates.Final(11).State);
        Assert.Equal(FileState.Failed, updates.Final(12).State);
        Assert.Equal(Path.Combine(outDir, "pic ü_waifu2x_2x.png"), updates.Final(10).OutputPath);
        Assert.Equal(Path.Combine(outDir, "pic ü_waifu2x_2x (1).png"), updates.Final(11).OutputPath);
        Assert.Equal("upscaled:img", File.ReadAllText(updates.Final(10).OutputPath!));
        Assert.Empty(Directory.EnumerateDirectories(TempRoot));
    }

    [Fact]
    public async Task Cancel_keeps_finished_outputs_and_cleans_temp()
    {
        var files = Enumerable.Range(0, 3).Select(i => new JobFile(i, Write($"src/{i}.png"), MediaKind.Image)).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var updates = new Updates(u =>
        {
            if (u is FileUpdate { State: FileState.Done })
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImagePipeline(new FakeEngine(StopAfter: 1), new ToolLocator(_dir.FullName)).RunAsync(files, Settings(), updates, cts.Token));

        Assert.Equal(FileState.Done, updates.Final(0).State);
        Assert.True(File.Exists(Path.Combine(_dir.FullName, "src", "0_waifu2x_2x.png")));
        Assert.Equal(FileState.Cancelled, updates.Final(1).State);
        Assert.Equal(FileState.Cancelled, updates.Final(2).State);
        Assert.Empty(Directory.EnumerateDirectories(TempRoot));
    }

    [Fact]
    public async Task Engine_crash_fails_files_without_output()
    {
        var files = new List<JobFile> { new(1, Write("x.png"), MediaKind.Image) };
        var updates = new Updates();

        await new ImagePipeline(new FakeEngine(ExitCode: 3), new ToolLocator(_dir.FullName))
            .RunAsync(files, Settings(), updates, TestContext.Current.CancellationToken);

        Assert.Equal(FileState.Failed, updates.Final(1).State);
        Assert.Contains("exit code 3", updates.Final(1).Detail, StringComparison.Ordinal);
    }

    private sealed class Updates(Action<JobEvent>? onReport = null) : IProgress<JobEvent>
    {
        private readonly ConcurrentDictionary<int, FileUpdate> _last = new();

        public void Report(JobEvent value)
        {
            if (value is FileUpdate u)
            {
                _last[u.FileId] = u;
            }

            onReport?.Invoke(value);
        }

        public FileUpdate Final(int id) => _last[id];
    }

    /// <summary>Copies inputs to outputs, prints ncnn-style lines; "BAD" content fails to decode.</summary>
    private sealed record FakeEngine(int StopAfter = int.MaxValue, int ExitCode = 0) : IUpscaler
    {
        public UpscalerDescriptor Descriptor => RealCuganNcnnVulkan.Definition;

        public string ExecutablePath => "fake";

        public bool IsInstalled => true;

        public string? Validate(UpscaleRequest request) => null;

        public IReadOnlyList<string> BuildArguments(UpscaleRequest request) => [];

        public Task<IReadOnlyList<EngineGpuDetected>> ProbeGpusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EngineGpuDetected>>([]);

        public async Task RunAsync(UpscaleRequest request, IProgress<EngineEvent>? progress, CancellationToken cancellationToken)
        {
            if (ExitCode != 0)
            {
                throw new EngineFailedException("fake", ExitCode, []);
            }

            var n = 0;
            foreach (var input in Directory.GetFiles(request.InputPath).Order(StringComparer.Ordinal))
            {
                if (n++ == StopAfter)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                // Mimic the engine's "dir/name" output format.
                var inLine = request.InputPath + "/" + Path.GetFileName(input);
                var content = await File.ReadAllTextAsync(input, cancellationToken);
                if (content == "BAD")
                {
                    progress?.Report(NcnnOutputParser.Parse($"decode image {inLine} failed"));
                    continue;
                }

                var output = Path.Combine(request.OutputPath, Path.GetFileNameWithoutExtension(input) + ".png");
                await File.WriteAllTextAsync(output, "upscaled:" + content, cancellationToken);
                progress?.Report(NcnnOutputParser.Parse($"{inLine} -> {request.OutputPath}/{Path.GetFileName(output)} done"));
            }
        }
    }
}
