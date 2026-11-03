using System.Collections.Concurrent;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class GifCommandTests
{
    [Fact]
    public void Parses_loop_count()
    {
        byte[] Ext(ushort loop) => [.. "GIF89a"u8, 0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 3, 1, (byte)loop, (byte)(loop >> 8), 0];
        Assert.Equal(0, GifCommands.ParseLoop(Ext(0)));
        Assert.Equal(300, GifCommands.ParseLoop(Ext(300)));
        Assert.Equal(-1, GifCommands.ParseLoop("GIF89a no extension"u8));
    }

    [Fact]
    public void Concat_list_gives_each_frame_its_delay_on_a_centisecond_timebase()
    {
        var list = GifCommands.ConcatList(["a.png", "b.png", "c.png"], [9, 20]);
        Assert.Equal(
            "ffconcat version 1.0\n"
            + "file 'a.png'\noption framerate 100\nduration 0.09\n"
            + "file 'b.png'\noption framerate 100\nduration 0.20\n"
            + "file 'c.png'\noption framerate 100\nduration 0.20\n",
            list);
    }

    [Fact]
    public void Palette_use_keeps_loop_and_final_delay()
    {
        var args = GifCommands.PaletteUse("l.txt", "p.png", -1, 20, "o.gif");
        Assert.Equal("-1", args[args.IndexOf("-loop") + 1]);
        Assert.Equal("20", args[args.IndexOf("-final_delay") + 1]);
        Assert.Equal("passthrough", args[args.IndexOf("-fps_mode") + 1]);
    }
}

/// <summary>GIF round trips through waifu2x + ffmpeg; skipped when either is missing.</summary>
public sealed class RealGifTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("w2xsg-gif-");
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _engines;

    public RealGifTests()
    {
        _engines = RepoEngines();
        Assert.SkipUnless(UpscalerCatalog.Create(UpscalerId.Waifu2x, _engines).IsInstalled, "waifu2x not installed");
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

    private async Task Ffmpeg(params string[] args)
    {
        var r = await ProcessRunner.RunAsync(_ffmpeg, ["-v", "error", "-y", .. args], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(r.ExitCode == 0, string.Join('\n', r.OutputTail));
    }

    private Task<Dictionary<int, FileUpdate>> Run(params string[] files) => Run(AnimationFormat.Gif, files);

    private async Task<Dictionary<int, FileUpdate>> Run(AnimationFormat format, params string[] files)
    {
        var settings = new AppSettings
        {
            GifOutput = format,
            VideoPreset = "ultrafast",
            Engine = UpscalerId.Waifu2x,
            Scale = 2,
            NoiseLevel = -1,
            EnginesDirectory = _engines,
            TempDirectory = P("tmp"),
            OutputDirectory = P("out"),
        };
        var final = new ConcurrentDictionary<int, FileUpdate>();
        var sink = new InlineSink(e =>
        {
            if (e is FileUpdate u)
            {
                final[u.FileId] = u;
            }
        });
        await JobRunner.RunAsync([.. files.Select((f, i) => new JobFile(i, f, MediaClassifier.Classify(f)))], settings, sink, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(P("tmp"), SettingsStore.AppFolderName)));
        return new Dictionary<int, FileUpdate>(final);
    }

    [Fact]
    public async Task Keeps_per_frame_delays_size_and_loop()
    {
        // Three frames with delays 9/20/9 cs, looping forever.
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=64x48:rate=1:duration=3", "-pix_fmt", "rgb24", P("f%d.png"));
        await File.WriteAllTextAsync(P("l.txt"), GifCommands.ConcatList(["f1.png", "f2.png", "f3.png"], [9, 20, 9]), TestContext.Current.CancellationToken);
        await Ffmpeg("-f", "concat", "-safe", "0", "-i", P("l.txt"), "-fps_mode", "passthrough", "-loop", "0", "-final_delay", "9", P("anim.gif"));
        var source = await GifCommands.ProbeAsync(_ffprobe, P("anim.gif"), TestContext.Current.CancellationToken);
        Assert.Equal([9, 20, 9], source.DelaysCs);

        var result = (await Run(P("anim.gif")))[0];

        Assert.Equal(FileState.Done, result.State);
        var output = await GifCommands.ProbeAsync(_ffprobe, result.OutputPath!, TestContext.Current.CancellationToken);
        Assert.Equal(source.DelaysCs, output.DelaysCs);
        Assert.Equal(0, output.Loop);
        var (_, size) = await ProcessRunner.CaptureAsync(_ffprobe, ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", result.OutputPath!], TestContext.Current.CancellationToken);
        Assert.Equal("128,96", size.Trim());
    }

    [Fact]
    public async Task Keeps_transparency_and_no_loop_and_treats_single_frame_gif_as_image()
    {
        // Transparent background with a moving red box, plays once.
        await Ffmpeg(
            "-f", "lavfi", "-i", "color=c=black@0.0:s=64x48:d=0.5:r=10,format=rgba",
            "-f", "lavfi", "-i", "color=c=red:s=16x16:d=0.5:r=10",
            "-filter_complex", "[0][1]overlay=x=t*40:y=16,split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=alpha_threshold=128",
            "-loop", "-1", P("trans.gif"));
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=32x24", "-frames:v", "1", P("still.gif"));

        var results = await Run(P("trans.gif"), P("still.gif"));

        Assert.Equal(FileState.Done, results[0].State);
        var trans = await GifCommands.ProbeAsync(_ffprobe, results[0].OutputPath!, TestContext.Current.CancellationToken);
        Assert.Equal(-1, trans.Loop);
        Assert.Equal(5, trans.DelaysCs.Count);
        // Top-left pixel of the first frame is still fully transparent.
        await Ffmpeg("-i", results[0].OutputPath!, "-frames:v", "1", "-vf", "crop=1:1:0:0,format=rgba", "-f", "rawvideo", P("px.raw"));
        Assert.Equal(0, (await File.ReadAllBytesAsync(P("px.raw"), TestContext.Current.CancellationToken))[3]);

        Assert.Equal(FileState.Done, results[1].State);
        Assert.EndsWith("still_waifu2x_2x.png", results[1].OutputPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Converts_to_apng_and_mp4_with_exact_timing()
    {
        await Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=64x48:rate=1:duration=3", "-pix_fmt", "rgb24", P("f%d.png"));
        await File.WriteAllTextAsync(P("l.txt"), GifCommands.ConcatList(["f1.png", "f2.png", "f3.png"], [9, 20, 9]), TestContext.Current.CancellationToken);
        await Ffmpeg("-f", "concat", "-safe", "0", "-i", P("l.txt"), "-fps_mode", "passthrough", "-loop", "0", "-final_delay", "9", P("anim.gif"));
        var ct = TestContext.Current.CancellationToken;

        var apng = (await Run(AnimationFormat.Apng, P("anim.gif")))[0];
        Assert.EndsWith("anim_waifu2x_2x.png", apng.OutputPath, StringComparison.Ordinal);
        var (_, apngTiming) = await ProcessRunner.CaptureAsync(_ffprobe, ["-v", "error", "-show_entries", "stream=codec_name:packet=duration_time", "-of", "csv=p=0", apng.OutputPath!], ct);
        Assert.Equal(["0.090000", "0.200000", "0.090000", "apng"], Lines(apngTiming));

        var mp4 = (await Run(AnimationFormat.Mp4, P("anim.gif")))[0];
        Assert.EndsWith("anim_waifu2x_2x.mp4", mp4.OutputPath, StringComparison.Ordinal);
        var (_, mp4Pts) = await ProcessRunner.CaptureAsync(_ffprobe, ["-v", "error", "-show_entries", "frame=pts_time", "-of", "csv=p=0", mp4.OutputPath!], ct);
        // Three frames at 0 / 0.09 / 0.29 s, plus the 1 cs repeat that carries the last delay.
        Assert.Equal(["0.000000", "0.090000", "0.290000", "0.380000"], Lines(mp4Pts));
    }

    private static IEnumerable<string> Lines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.TrimEnd(','));

    private sealed class InlineSink(Action<JobEvent> handler) : IProgress<JobEvent>
    {
        public void Report(JobEvent value) => handler(value);
    }
}
