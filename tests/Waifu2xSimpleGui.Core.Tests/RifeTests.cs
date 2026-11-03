using System.Collections.Concurrent;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class RifeArgumentTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "w2xsg-engines");

    [Fact]
    public void Builds_directory_mode_arguments()
    {
        var rife = RifeNcnnVulkan.Create(Root);
        var args = rife.BuildArguments("in", "out", 62, 1080, new AppSettings { InterpolationFactor = 2, GpuIds = [0, 1] }).ToList();

        Assert.Equal("62", args[args.IndexOf("-n") + 1]);
        Assert.Equal(Path.Combine(Root, "rife-ncnn-vulkan", "rife-v4.6"), args[args.IndexOf("-m") + 1]);
        Assert.Equal("1:2,2:2", args[args.IndexOf("-j") + 1]);
        Assert.Equal("0,1", args[args.IndexOf("-g") + 1]);
        Assert.DoesNotContain("-u", args);
        Assert.Contains("-u", rife.BuildArguments("in", "out", 62, 2160, new AppSettings()));
    }

    [Theory]
    [InlineData("rife-v4.6", 3, null)]
    [InlineData("rife-v3.1", 2, null)]
    [InlineData("rife-v3.1", 3, "only does 2x")]
    [InlineData("rife-nope", 2, "Unknown")]
    [InlineData("rife-v4", 9, "2–8")]
    public void Validates_model_and_factor(string model, int factor, string? error)
    {
        var result = RifeNcnnVulkan.Validate(new AppSettings { RifeModel = model, InterpolationFactor = factor });
        if (error is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(error, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parses_real_rife_output()
    {
        var events = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rife-20221029-dir.txt")).Select(NcnnOutputParser.Parse).ToList();
        Assert.Equal(12, events.OfType<EngineFileCompleted>().Count());
        Assert.Single(events.OfType<EngineGpuDetected>());
    }
}

/// <summary>Frame interpolation end to end; skipped when RIFE/Real-ESRGAN/ffmpeg are missing.</summary>
public sealed class RealRifeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("w2xsg-rife-");
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _engines;

    public RealRifeTests()
    {
        _engines = RepoEngines();
        Assert.SkipUnless(RifeNcnnVulkan.Create(_engines).IsInstalled, "RIFE not installed");
        Assert.SkipUnless(UpscalerCatalog.Create(UpscalerId.RealEsrgan, _engines).IsInstalled, "Real-ESRGAN not installed");
        var tools = new ToolLocator(_engines);
        _ffmpeg = tools.FindFfmpeg() ?? "";
        _ffprobe = tools.FindFfprobe() ?? "";
        Assert.SkipWhen(_ffmpeg.Length == 0 || _ffprobe.Length == 0, "ffmpeg/ffprobe not found");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static string RepoEngines()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Waifu2xSimpleGui.slnx")))
            {
                return Path.Combine(dir.FullName, "engines");
            }
        }

        return "missing";
    }

    private string P(string name) => Path.Combine(_dir.FullName, name);

    private async Task<FileUpdate> Run(string source, AppSettings overrides)
    {
        var settings = overrides with
        {
            Engine = UpscalerId.RealEsrgan,
            Model = "realesr-animevideov3",
            Scale = 2,
            EnginesDirectory = _engines,
            TempDirectory = P("tmp"),
            OutputDirectory = P("out"),
            VideoPreset = "ultrafast",
            VideoSegmentFrames = 30,
        };
        Assert.Null(JobRunner.Validate(settings));
        var final = new ConcurrentDictionary<int, FileUpdate>();
        await JobRunner.RunAsync([new JobFile(0, source, MediaKind.Video)], settings, new Sink(final), TestContext.Current.CancellationToken);
        Assert.True(final[0].State == FileState.Done, final[0].Detail);
        return final[0];
    }

    private async Task<string> Probe(string file, string entries)
    {
        var (_, stdout) = await ProcessRunner.CaptureAsync(
            _ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", entries, "-of", "csv=p=0", file], TestContext.Current.CancellationToken);
        return stdout.Trim();
    }

    private async Task<string> MakeSource(int frames)
    {
        var src = P("src.mp4");
        var r = await ProcessRunner.RunAsync(
            _ffmpeg,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24", "-frames:v", frames.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", src],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, r.ExitCode);
        return src;
    }

    [Fact]
    public async Task Interpolate_only_doubles_frames_without_duplicates_at_segment_boundaries()
    {
        // 84 frames → segments 30/30/24; lossless output so identical frames hash identically.
        var src = await MakeSource(84);
        var result = await Run(src, new AppSettings { InterpolationFactor = 2, InterpolateOnly = true, VideoCrf = 0 });

        Assert.EndsWith("src_rife_1x.mp4", result.OutputPath, StringComparison.Ordinal);
        Assert.Equal("160,90,48/1,168", await Probe(result.OutputPath!, "stream=width,height,r_frame_rate,nb_read_frames"));

        var (_, md5) = await ProcessRunner.CaptureAsync(_ffmpeg, ["-v", "error", "-i", result.OutputPath!, "-f", "framemd5", "-"], TestContext.Current.CancellationToken);
        var hashes = md5.Split('\n').Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(',')[^1].Trim()).ToList();
        var repeats = Enumerable.Range(1, hashes.Count - 1).Where(i => hashes[i] == hashes[i - 1]).ToList();
        // Only the held last frame repeats (RIFE has nothing to interpolate towards).
        Assert.Equal([167], repeats);
    }

    [Fact]
    public async Task Upscale_then_interpolate_3x_with_v4()
    {
        var src = await MakeSource(40);
        var result = await Run(src, new AppSettings { InterpolationFactor = 3, RifeModel = "rife-v4" });

        Assert.EndsWith("src_realesrgan_2x.mp4", result.OutputPath, StringComparison.Ordinal);
        Assert.Equal("320,180,72/1,120", await Probe(result.OutputPath!, "stream=width,height,r_frame_rate,nb_read_frames"));
    }

    private sealed class Sink(ConcurrentDictionary<int, FileUpdate> final) : IProgress<JobEvent>
    {
        public void Report(JobEvent value)
        {
            if (value is FileUpdate u)
            {
                final[u.FileId] = u;
            }
        }
    }
}
