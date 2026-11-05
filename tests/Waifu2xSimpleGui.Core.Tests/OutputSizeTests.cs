using System.Collections.Concurrent;
using System.Globalization;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Pipelines;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class OutputSizeTests
{
    [Theory]
    [InlineData(UpscalerId.RealCugan, "models-se", 3.0, 3)]
    [InlineData(UpscalerId.Waifu2x, "models-cunet", 3.0, 4)]
    [InlineData(UpscalerId.Waifu2x, "models-cunet", 1.5, 2)]
    [InlineData(UpscalerId.RealEsrgan, "realesrgan-x4plus", 2.0, 4)]
    [InlineData(UpscalerId.RealEsrgan, "realesrgan-x4plus", 8.0, 4)]
    public void Factor_mode_picks_smallest_model_scale_at_least_the_factor(UpscalerId engine, string model, double factor, int expected) =>
        Assert.Equal(expected, OutputSize.EngineScale(new AppSettings { Engine = engine, Model = model, SizeMode = OutputSizeMode.Factor, TargetFactor = factor }));

    [Fact]
    public void Filters()
    {
        var factor = new AppSettings { SizeMode = OutputSizeMode.Factor, TargetFactor = 3 };
        Assert.Equal(@"scale=max(1\,round(iw*0.75)):max(1\,round(ih*0.75)):flags=lanczos", OutputSize.Filter(factor, 4, even: false));
        Assert.Equal("scale=trunc(iw*0.75/2+0.5)*2:trunc(ih*0.75/2+0.5)*2:flags=lanczos", OutputSize.Filter(factor, 4, even: true));
        Assert.Null(OutputSize.Filter(factor, 3, even: false));
        Assert.Null(OutputSize.Filter(new AppSettings(), 2, even: false));

        var fit = new AppSettings { SizeMode = OutputSizeMode.Fit, FitWidth = 1920, FitHeight = 1080 };
        Assert.Equal(@"scale='min(iw\,1920)':'min(ih\,1080)':force_original_aspect_ratio=decrease:flags=lanczos", OutputSize.Filter(fit, 2, even: false));
    }

    [Fact]
    public void Scale_token_shows_the_target_factor()
    {
        var s = new AppSettings { SizeMode = OutputSizeMode.Factor, TargetFactor = 1.5, Scale = 2 };
        Assert.Equal("a_waifu2x_1.5x", OutputNaming.ExpandTemplate(s, "a.png"));
        Assert.Matches(OutputNaming.OutputPattern(s.FileNameTemplate)!, "a_waifu2x_1.5x.png");
    }

    [Fact]
    public void Resize_only_skips_the_engine_scale_and_names_outputs_resize()
    {
        var s = new AppSettings { Engine = UpscalerId.Waifu2x, SizeMode = OutputSizeMode.Factor, TargetFactor = 0.5, ResizeOnly = true };
        Assert.Equal(1, OutputSize.EngineScale(s));
        Assert.Equal(@"scale=max(1\,round(iw*0.5)):max(1\,round(ih*0.5)):flags=lanczos", OutputSize.Filter(s, 1, even: false));
        Assert.Equal("a_waifu2x_4x_resize_0.5x", OutputNaming.ExpandTemplate(s, "a_waifu2x_4x.png"));

        // Model scale has nothing to resize: the flag is ignored.
        Assert.False((s with { SizeMode = OutputSizeMode.ModelScale }).SkipsUpscale);
    }

    [Theory]
    [InlineData("a_waifu2x_4x.png", false, true)]
    [InlineData("a_waifu2x_4x_resize_0.5x.png", true, true)]
    [InlineData("a_waifu2x_4x.png", true, false)]
    [InlineData("a.png", false, false)]
    public void Resize_only_treats_upscaled_files_as_inputs(string file, bool resizeOnly, bool skipped) =>
        Assert.Equal(skipped, OutputNaming.OutputPattern("{name}_{engine}_{scale}x", resizeOnly)!.IsMatch(file));

    [Fact]
    public void Size_token_shows_the_fit_box_or_the_factor()
    {
        var fit = new AppSettings { SizeMode = OutputSizeMode.Fit, FitWidth = 1920, FitHeight = 1080, ResizeOnly = true, FileNameTemplate = "{name}_{engine}_{size}" };
        Assert.Equal("a_resize_1920x1080", OutputNaming.ExpandTemplate(fit, "a.png"));
        Assert.Equal("a_resize_0.5x", OutputNaming.ExpandTemplate(fit with { SizeMode = OutputSizeMode.Factor, TargetFactor = 0.5 }, "a.png"));
        Assert.Equal("a_waifu2x_4x", OutputNaming.ExpandTemplate(fit with { SizeMode = OutputSizeMode.ModelScale, Scale = 4 }, "a.png"));

        var pattern = OutputNaming.OutputPattern(fit.FileNameTemplate)!;
        Assert.Matches(pattern, "a_resize_1920x1080.png");
        Assert.Matches(pattern, "a_resize_0.5x (2).png");
        Assert.DoesNotMatch(pattern, "a_resize_big.png");
    }

    [Theory]
    [InlineData("Disk quota exceeded : '/tmp/x/00000001.png'", true)]
    [InlineData("No space left on device", true)]
    [InlineData("There is not enough space on the disk.", true)]
    [InlineData("ffmpeg encode failed (exit 1): Invalid data", false)]
    public void Disk_full_errors_point_at_the_temp_folder(string message, bool hinted) =>
        Assert.Equal(hinted, JobRunner.WithDiskFullHint(message).Contains("Temp folder", StringComparison.Ordinal));

    [Fact]
    public void Resize_only_without_engine_token_skips_nothing() =>
        Assert.Null(OutputNaming.OutputPattern("{name}_{scale}x", resizeOnly: true));
}

/// <summary>Resize step end to end (waifu2x / Real-ESRGAN + ffmpeg); skipped when missing.</summary>
public sealed class RealOutputSizeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("w2xsg-size-");
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _engines;

    public RealOutputSizeTests()
    {
        _engines = RepoEngines();
        Assert.SkipUnless(UpscalerCatalog.Create(UpscalerId.Waifu2x, _engines).IsInstalled, "waifu2x not installed");
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

    private async Task Make(string lavfi, string output, params string[] extra)
    {
        var r = await ProcessRunner.RunAsync(_ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", lavfi, .. extra, output], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, r.ExitCode);
    }

    private async Task<string> Size(string file)
    {
        var (_, stdout) = await ProcessRunner.CaptureAsync(_ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", file], TestContext.Current.CancellationToken);
        return stdout.Trim();
    }

    private async Task<Dictionary<int, FileUpdate>> Run(AppSettings settings, params string[] files)
    {
        settings = settings with
        {
            EnginesDirectory = _engines,
            TempDirectory = P("tmp"),
            OutputDirectory = P("out"),
            VideoPreset = "ultrafast",
            VideoSegmentFrames = 30,
        };
        Assert.Null(JobRunner.Validate(settings));
        var final = new ConcurrentDictionary<int, FileUpdate>();
        await JobRunner.RunAsync([.. files.Select((f, i) => new JobFile(i, f, MediaClassifier.Classify(f)))], settings, new Sink(final), TestContext.Current.CancellationToken);
        foreach (var u in final.Values)
        {
            Assert.True(u.State == FileState.Done, u.Detail);
        }

        return new Dictionary<int, FileUpdate>(final);
    }

    [Fact]
    public async Task Factor_3_on_waifu2x_runs_4x_then_downscales_images_and_gifs()
    {
        await Make("testsrc2=size=16x12", P("a.png"), "-frames:v", "1");
        await Make("testsrc2=size=16x12:rate=10:duration=0.3", P("b.gif"));
        var settings = new AppSettings { Engine = UpscalerId.Waifu2x, NoiseLevel = -1, SizeMode = OutputSizeMode.Factor, TargetFactor = 3 };

        var results = await Run(settings, P("a.png"), P("b.gif"));

        Assert.EndsWith("a_waifu2x_3x.png", results[0].OutputPath, StringComparison.Ordinal);
        Assert.Equal("48,36", await Size(results[0].OutputPath!));
        Assert.Equal("48,36", await Size(results[1].OutputPath!));
    }

    [Fact]
    public async Task Fit_downscales_into_the_box_keeping_aspect()
    {
        await Make("testsrc2=size=40x30", P("a.png"), "-frames:v", "1");
        var settings = new AppSettings { Engine = UpscalerId.Waifu2x, NoiseLevel = -1, Scale = 2, SizeMode = OutputSizeMode.Fit, FitWidth = 60, FitHeight = 100 };

        var results = await Run(settings, P("a.png"));

        Assert.Equal("60,45", await Size(results[0].OutputPath!)); // 80x60 → fits 60 wide
    }

    [Fact]
    public async Task Factor_on_video_gives_even_size()
    {
        await Make("testsrc2=size=160x90:rate=24", P("v.mp4"), "-frames:v", "20", "-c:v", "libx264");
        var settings = new AppSettings { Engine = UpscalerId.RealEsrgan, Model = "realesr-animevideov3", SizeMode = OutputSizeMode.Factor, TargetFactor = 1.5 };

        var results = await Run(settings, P("v.mp4"));

        Assert.Equal("240,136", await Size(results[0].OutputPath!)); // 2x engine, then 0.75 → 240x135 → even
        var (_, frames) = await ProcessRunner.CaptureAsync(_ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", results[0].OutputPath!], TestContext.Current.CancellationToken);
        Assert.Equal(20, int.Parse(frames.Trim(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Resize_only_resizes_images_gifs_and_videos_without_the_engine()
    {
        await Make("testsrc2=size=64x48", P("a.png"), "-frames:v", "1");
        await Make("testsrc2=size=64x48:rate=10:duration=0.3", P("b.gif"));
        await Make("testsrc2=size=160x90:rate=24", P("v.mp4"), "-frames:v", "20", "-c:v", "libx264");
        var settings = new AppSettings { SizeMode = OutputSizeMode.Factor, TargetFactor = 0.5, ResizeOnly = true };

        var results = await Run(settings, P("a.png"), P("b.gif"), P("v.mp4"));

        Assert.EndsWith("a_resize_0.5x.png", results[0].OutputPath, StringComparison.Ordinal);
        Assert.Equal("32,24", await Size(results[0].OutputPath!));
        Assert.Equal("32,24", await Size(results[1].OutputPath!));
        Assert.Equal("80,46", await Size(results[2].OutputPath!)); // 80x45 → even

        var fit = await Run(settings with { SizeMode = OutputSizeMode.Fit, FitWidth = 40, FitHeight = 40 }, P("a.png"));
        Assert.Equal("40,30", await Size(fit[0].OutputPath!));
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
