using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class VideoCommandTests
{
    private static VideoInfo Fixture(string name) =>
        MediaProbe.ParseVideo(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    private static string ArgValue(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Probe_parses_cfr_sample()
    {
        var v = Fixture("ffprobe-sample1.json");
        Assert.Equal(new Rational(3267, 125), v.FrameRate);
        Assert.False(v.IsVfr);
        Assert.Equal(79, v.EstimatedFrames);
        Assert.Equal((640, 480), (v.Width, v.Height));
        Assert.Equal("bt601", v.ColorMatrix); // untagged SD
        Assert.Contains(v.Streams, s => s.CodecType == "audio");
    }

    [Fact]
    public void Probe_skips_cover_art_and_detects_vfr()
    {
        var v = Fixture("ffprobe-vfr-cover.json");
        Assert.Equal(1, v.StreamIndex);
        Assert.True(v.IsVfr);
        Assert.Equal(new Rational(690, 29), v.FrameRate);
        Assert.Equal("bt709", v.ColorMatrix);
    }

    [Fact]
    public void Frame_times_sort_b_frames_and_end_with_the_last_duration()
    {
        // Decode order (B-frames), one packet without a duration, a trailing comma like ffprobe emits.
        var t = MediaProbe.ParseFrameTimes("1.0,0.1\n1.3,0.1\n1.1,N/A\n1.2,0.05,\n");
        Assert.Equal([0, 0.1, 0.2, 0.3, 0.4], t.Select(x => Math.Round(x, 6)));

        var v = Fixture("ffprobe-sample1.json") with { FrameTimes = t, VideoOffset = 1 };
        Assert.Equal("1.15", ArgValue(VideoCommands.ExtractSegment("in.mp4", v, 1, 2, "d"), "-ss")); // between frames 1 and 2
        Assert.Equal([0.05, 0.05, 0.05, 0.05], VideoCommands.OutputDurations(t, 2, 2, 2).Select(x => Math.Round(x, 6)));
    }

    [Fact]
    public void Extract_seeks_half_a_frame_before_the_segment()
    {
        var v = Fixture("ffprobe-sample1.json") with { VideoOffset = 0.5 };
        var first = VideoCommands.ExtractSegment("in.mp4", v, 0, 100, "d");
        Assert.DoesNotContain("-ss", first);

        var third = VideoCommands.ExtractSegment("in.mp4", v, 2, 100, "d");
        var expected = 0.5 + (199.5 * 125 / 3267);
        Assert.Equal(expected, double.Parse(ArgValue(third, "-ss"), System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Equal("100", ArgValue(third, "-frames:v"));
        Assert.Equal("0:0", ArgValue(third, "-map"));
        Assert.StartsWith("scale=in_color_matrix=bt601:in_range=tv", ArgValue(third, "-vf"), StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_normalizes_vfr_and_escapes_percent()
    {
        var v = Fixture("ffprobe-vfr-cover.json");
        var args = VideoCommands.ExtractSegment("in.mp4", v, 0, 50, Path.Combine("100%", "in"));
        Assert.StartsWith("fps=690/29,", ArgValue(args, "-vf"), StringComparison.Ordinal);
        Assert.Equal(Path.Combine("100%%", "in", "%08d.png"), args[^1]);
    }

    [Theory]
    [InlineData("libx264", "-crf", "18")]
    [InlineData("libx265", "-crf", "18")]
    [InlineData("h264_nvenc", "-cq", "18")]
    [InlineData("hevc_amf", "-qp_i", "18")]
    [InlineData("h264_qsv", "-global_quality", "18")]
    [InlineData("hevc_videotoolbox", "-q:v", "64")]
    public void Encoder_quality_flag_per_family(string encoder, string flag, string value)
    {
        var args = VideoCommands.EncoderArguments(encoder, 18, "slow");
        Assert.Equal(encoder, ArgValue(args, "-c:v"));
        Assert.Equal(value, ArgValue(args, flag));
    }

    [Fact]
    public void Encode_tags_color_and_pads_to_even()
    {
        var v = Fixture("ffprobe-vfr-cover.json");
        var args = VideoCommands.EncodeSegment("frames", v, new AppSettings(), "seg.part");
        Assert.Equal("690/29", ArgValue(args, "-framerate"));
        Assert.Equal("pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", ArgValue(args, "-vf"));
        Assert.Equal("bt709", ArgValue(args, "-colorspace"));
        Assert.Equal("mp4", ArgValue(args, "-f"));
    }

    [Fact]
    public void Mux_maps_text_subs_for_mp4_and_offsets_video()
    {
        var v = Fixture("ffprobe-sample1.json") with
        {
            VideoOffset = 0.024,
            Streams = [new(0, "video", "h264"), new(1, "audio", "aac"), new(2, "subtitle", "subrip"), new(3, "subtitle", "hdmv_pgs_subtitle")],
        };

        var mp4 = VideoCommands.Mux("list.txt", "src.mkv", v, "out", mkv: false, safeMode: false);
        Assert.Equal("0.024", ArgValue(mp4, "-itsoffset"));
        Assert.Contains("1:2", mp4);
        Assert.DoesNotContain("1:3", mp4);
        Assert.Equal("mov_text", ArgValue(mp4, "-c:s"));

        var mkv = VideoCommands.Mux("list.txt", "src.mkv", v, "out", mkv: true, safeMode: false);
        Assert.Contains("1:s?", mkv);
        Assert.Contains("1:t?", mkv);

        var safe = VideoCommands.Mux("list.txt", "src.mkv", v, "out", mkv: false, safeMode: true);
        Assert.Equal("aac", ArgValue(safe, "-c:a"));
        Assert.DoesNotContain("1:2", safe);
    }
}
