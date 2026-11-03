using System.Collections.Concurrent;
using System.Globalization;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

/// <summary>End-to-end video runs with Real-ESRGAN + ffmpeg; skipped when either is missing.</summary>
public sealed class RealVideoTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("w2xsg-video-");
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _engines;

    public RealVideoTests()
    {
        _engines = FindEngines();
        var tools = new ToolLocator(_engines);
        Assert.SkipUnless(UpscalerCatalog.Create(UpscalerId.RealEsrgan, _engines).IsInstalled, "Real-ESRGAN not installed");
        _ffmpeg = tools.FindFfmpeg() ?? "";
        _ffprobe = tools.FindFfprobe() ?? "";
        Assert.SkipWhen(_ffmpeg.Length == 0 || _ffprobe.Length == 0, "ffmpeg/ffprobe not found");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static string FindEngines()
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

    private AppSettings Settings() => new()
    {
        Engine = UpscalerId.RealEsrgan,
        Model = "realesr-animevideov3",
        Scale = 2,
        EnginesDirectory = _engines,
        TempDirectory = Path.Combine(_dir.FullName, "tmp"),
        OutputDirectory = Path.Combine(_dir.FullName, "out"),
        VideoPreset = "ultrafast",
        VideoSegmentFrames = 30,
    };

    private async Task Ffmpeg(params string[] args)
    {
        var r = await ProcessRunner.RunAsync(_ffmpeg, ["-v", "error", "-y", .. args], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(r.ExitCode == 0, string.Join('\n', r.OutputTail));
    }

    private async Task<string> Probe(string file, string entries, string stream = "v:0")
    {
        var (_, stdout) = await ProcessRunner.CaptureAsync(
            _ffprobe,
            ["-v", "error", "-count_frames", "-select_streams", stream, "-show_entries", entries, "-of", "csv=p=0", file],
            TestContext.Current.CancellationToken);
        return stdout.Trim();
    }

    [Fact]
    public async Task Upscales_segmented_video_keeping_audio_subtitles_and_sync()
    {
        var src = Path.Combine(_dir.FullName, "clip ü.mkv");
        var srt = Path.Combine(_dir.FullName, "s.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:02,000\nhello\n", TestContext.Current.CancellationToken);
        // 84 frames at an awkward rate → segments of 30/30/24; video starts 0.1 s after audio.
        await Ffmpeg(
            "-itsoffset", "0.1", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=3267/125",
            "-f", "lavfi", "-i", "sine=f=440:duration=4", "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:s", "-frames:v", "84", "-c:v", "libx264", "-c:a", "aac", src);

        var updates = new Sink();
        await JobRunner.RunAsync([new JobFile(0, src, MediaKind.Video)], Settings(), updates, TestContext.Current.CancellationToken);

        var final = updates.Final[0];
        Assert.True(final.State == FileState.Done, final.Detail);
        Assert.Equal(Path.Combine(_dir.FullName, "out", "clip ü_realesrgan_2x.mkv"), final.OutputPath);
        Assert.Equal("320,180,3267/125,84", await Probe(final.OutputPath!, "stream=width,height,nb_read_frames,r_frame_rate"));
        Assert.Equal("aac", await Probe(final.OutputPath!, "stream=codec_name", "a:0"));
        Assert.Equal(await Probe(src, "stream=codec_name", "s:0"), await Probe(final.OutputPath!, "stream=codec_name", "s:0"));

        // A/V offset preserved within one frame.
        static double Start(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        var srcOffset = Start(await Probe(src, "stream=start_time")) - Start(await Probe(src, "stream=start_time", "a:0"));
        var outOffset = Start(await Probe(final.OutputPath!, "stream=start_time")) - Start(await Probe(final.OutputPath!, "stream=start_time", "a:0"));
        Assert.Equal(srcOffset, outOffset, 0.042);

        Assert.Equal(["resume"], Directory.EnumerateFileSystemEntries(TempRoot).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(TempRoot, "resume")));
    }

    private string TempRoot => Path.Combine(_dir.FullName, "tmp", SettingsStore.AppFolderName);

    [Fact]
    public async Task Cancel_then_restart_resumes_from_encoded_segments()
    {
        var src = Path.Combine(_dir.FullName, "long.mp4");
        // Exactly 5 × 30 frames: the last extraction past the end must yield nothing, not fail.
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30", "-frames:v", "150", "-c:v", "libx264", src);
        var settings = Settings() with { OutputDirectory = null };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new Sink(e =>
        {
            // Cancel as soon as one segment is committed.
            if (e is FileProgress && Directory.EnumerateFiles(TempRoot, "manifest.txt", SearchOption.AllDirectories).Any())
            {
                cts.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JobRunner.RunAsync([new JobFile(0, src, MediaKind.Video)], settings, first, cts.Token));
        Assert.Equal(FileState.Cancelled, first.Final[0].State);

        var second = new Sink();
        await JobRunner.RunAsync([new JobFile(0, src, MediaKind.Video)], settings, second, TestContext.Current.CancellationToken);
        Assert.Equal(FileState.Done, second.Final[0].State);
        Assert.Contains(second.Logs, l => l.Contains("resuming", StringComparison.Ordinal));
        Assert.Equal("150", await Probe(second.Final[0].OutputPath!, "stream=nb_read_frames"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(TempRoot, "resume")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Vfr_passthrough_keeps_every_frame_time(int interpolation)
    {
        Assert.SkipWhen(interpolation > 1 && !RifeNcnnVulkan.Create(_engines).IsInstalled, "RIFE not installed");
        var src = Path.Combine(_dir.FullName, "vfr.mp4");
        // 25 frames at 30 fps, then 45 at ~12 fps with jitter; the rate change falls inside segment 0 of 30.
        await Ffmpeg(
            "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30", "-frames:v", "70",
            "-vf", "setpts='if(lt(N,25),N/30,25/30+(N-25)/12+mod(N,3)*0.01)/TB'",
            "-fps_mode", "passthrough", "-video_track_timescale", "90000", "-c:v", "libx264", src);
        var settings = Settings() with { VfrPassthrough = true, InterpolationFactor = interpolation };

        var updates = new Sink();
        await JobRunner.RunAsync([new JobFile(0, src, MediaKind.Video)], settings, updates, TestContext.Current.CancellationToken);
        var final = updates.Final[0];
        Assert.True(final.State == FileState.Done, final.Detail);

        // Source times plus a trailing end time (the last packet's duration), as the pipeline sees them.
        var source = MediaProbe.ParseFrameTimes(await Probe(src, "packet=pts_time,duration_time"));
        var expected = source.Take(source.Count - 1).ToList();
        if (interpolation > 1)
        {
            expected = [.. expected.SelectMany((t, i) => new[] { t, (t + source[i + 1]) / 2 })]; // + midpoints
        }

        var actual = Times(await Probe(final.OutputPath!, "packet=pts_time"));
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) < 0.002, $"frame {i}: {actual[i]:0.####} s, expected {expected[i]:0.####} s");
        }
    }

    /// <summary>Sorted PTS relative to the first frame.</summary>
    private static List<double> Times(string csv)
    {
        var t = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s.TrimEnd(','), CultureInfo.InvariantCulture)).Order().ToList();
        return [.. t.Select(x => x - t[0])];
    }

    private sealed class Sink(Action<JobEvent>? onReport = null) : IProgress<JobEvent>
    {
        public ConcurrentDictionary<int, FileUpdate> Final { get; } = new();

        public ConcurrentQueue<string> Logs { get; } = new();

        public void Report(JobEvent value)
        {
            switch (value)
            {
                case FileUpdate u:
                    Final[u.FileId] = u;
                    break;
                case JobLog l:
                    Logs.Enqueue(l.Message);
                    break;
            }

            onReport?.Invoke(value);
        }
    }
}
