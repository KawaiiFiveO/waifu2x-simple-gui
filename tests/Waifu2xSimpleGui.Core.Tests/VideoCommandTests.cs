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

        Assert.Equal([0.05, 0.05, 0.05, 0.05], VideoCommands.OutputDurations(t, 2, 2, 2).Select(x => Math.Round(x, 6)));
    }

    [Fact]
    public void Extract_is_one_pass_to_a_png_pipe_and_resume_selects_instead_of_seeking()
    {
        var v = Fixture("ffprobe-sample1.json") with { VideoOffset = 0.5 };
        var all = VideoCommands.ExtractFrames("in.mp4", v);
        Assert.DoesNotContain("-ss", all);
        Assert.Equal("0:0", ArgValue(all, "-map"));
        Assert.Equal("scale=in_color_matrix=bt601:in_range=tv,format=rgb24", ArgValue(all, "-vf"));
        Assert.Equal(["-f", "image2pipe", "pipe:1"], all[^3..]);

        var resumed = VideoCommands.ExtractFrames("in.mp4", v, 1200);
        Assert.DoesNotContain("-ss", resumed);
        Assert.StartsWith("select=gte(n\\,1200),scale=", ArgValue(resumed, "-vf"), StringComparison.Ordinal);
        Assert.Equal("00000001.png", VideoCommands.FrameName(0));
    }

    [Fact]
    public void Extract_filter_order_deinterlace_then_fps_then_select()
    {
        var v = Fixture("ffprobe-vfr-cover.json") with { Interlaced = true };
        Assert.StartsWith("bwdif=mode=send_frame:parity=auto:deint=interlaced,fps=690/29,select=gte(n\\,30),", ArgValue(VideoCommands.ExtractFrames("in.mp4", v, 30), "-vf"), StringComparison.Ordinal);
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

        var mp4 = VideoCommands.Mux("list.txt", "src.mkv", v, "out", VideoContainer.Mp4, "libx264", safeMode: false);
        Assert.Equal("0.024", ArgValue(mp4, "-itsoffset"));
        Assert.Contains("1:2", mp4);
        Assert.DoesNotContain("1:3", mp4);
        Assert.Equal("mov_text", ArgValue(mp4, "-c:s"));
        Assert.Equal("mp4", mp4[^2]);
        Assert.DoesNotContain("-tag:v", mp4);

        var mkv = VideoCommands.Mux("list.txt", "src.mkv", v, "out", VideoContainer.Mkv, "libx265", safeMode: false);
        Assert.Contains("1:s?", mkv);
        Assert.Contains("1:t?", mkv);
        Assert.DoesNotContain("-tag:v", mkv);

        var safe = VideoCommands.Mux("list.txt", "src.mkv", v, "out", VideoContainer.Mp4, "libx264", safeMode: true);
        Assert.Equal("aac", ArgValue(safe, "-c:a"));
        Assert.DoesNotContain("1:2", safe);

        var mov = VideoCommands.Mux("list.txt", "src.mp4", v, "out", VideoContainer.Mov, "hevc_amf", safeMode: false);
        Assert.Equal("mov", mov[^2]);
        Assert.Equal("hvc1", ArgValue(mov, "-tag:v"));
        Assert.Equal("mov_text", ArgValue(mov, "-c:s"));
    }

    [Fact]
    public void Copy_fallback_message_names_the_rejected_codec()
    {
        string[] tail =
        [
            "[mp4 @ 000001b001bc8380] Could not find tag for codec binkaudio_dct in stream #1, codec not currently supported in container",
            "[out#0/mp4 @ 000001c8493896c0] Could not write header (incorrect codec parameters ?): Invalid argument",
        ];
        Assert.Equal("binkaudio_dct can't be stored in MP4 — audio converted to AAC.", VideoCommands.CopyFallbackMessage(tail, VideoContainer.Mp4, hasSubtitles: false));
        Assert.Equal("Audio/subtitles couldn't be copied into MKV — audio converted to AAC, subtitles dropped.", VideoCommands.CopyFallbackMessage(["other error"], VideoContainer.Mkv, hasSubtitles: true));
    }

    [Theory]
    [InlineData("a.mkv", VideoContainer.Auto, "libx264", VideoContainer.Mkv)]
    [InlineData("a.webm", VideoContainer.Auto, VideoCommands.ProRes, VideoContainer.Mkv)]
    [InlineData("a.mp4", VideoContainer.Auto, VideoCommands.ProRes, VideoContainer.Mov)]
    [InlineData("a.bik", VideoContainer.Auto, "libx264", VideoContainer.Mp4)]
    [InlineData("a.mkv", VideoContainer.Mov, "libx264", VideoContainer.Mov)]
    public void Container_auto_rule(string source, VideoContainer chosen, string encoder, VideoContainer expected) =>
        Assert.Equal(expected, VideoCommands.ResolveContainer(source, chosen, encoder));

    [Fact]
    public void Container_rejects_impossible_combinations()
    {
        Assert.NotNull(VideoCommands.ContainerError(VideoCommands.ProRes, VideoContainer.Mp4));
        Assert.NotNull(VideoCommands.ContainerError("libsvtav1", VideoContainer.Mov));
        Assert.NotNull(VideoCommands.ContainerError("av1_amf", VideoContainer.Mov));
        Assert.Null(VideoCommands.ContainerError(VideoCommands.ProRes, VideoContainer.Mkv));
        Assert.Null(VideoCommands.ContainerError("libsvtav1", VideoContainer.Auto));
        Assert.Null(VideoCommands.ContainerError("hevc_nvenc", VideoContainer.Mov));
    }

    [Fact]
    public void ProRes_uses_profile_422_10bit_and_mov_segments()
    {
        var args = VideoCommands.EncodeSegment("frames", Fixture("ffprobe-sample1.json"), new AppSettings { VideoEncoder = VideoCommands.ProRes, VideoPreset = "lt" }, "seg.part");
        Assert.Equal("lt", ArgValue(args, "-profile:v"));
        Assert.EndsWith("format=yuv422p10le", ArgValue(args, "-vf"), StringComparison.Ordinal);
        Assert.Equal("mov", ArgValue(args, "-f"));
        Assert.Equal("hq", ArgValue(VideoCommands.EncoderArguments(VideoCommands.ProRes, 18, "medium"), "-profile:v"));
    }

    [Fact]
    public void Interlaced_anamorphic_vob_is_deinterlaced_and_keeps_its_pixel_aspect()
    {
        var v = Fixture("ffprobe-interlaced-vob.json");
        Assert.True(v.Interlaced);
        Assert.False(v.IsVfr);
        Assert.Equal(new Rational(30000, 1001), v.FrameRate);
        Assert.Equal(new Rational(32, 27), v.SampleAspect);
        Assert.StartsWith("bwdif=mode=send_frame:parity=auto:deint=interlaced,", ArgValue(VideoCommands.ExtractFrames("in.vob", v), "-vf"), StringComparison.Ordinal);
        Assert.EndsWith(",setsar=32/27", ArgValue(VideoCommands.EncodeSegment("frames", v, new AppSettings(), "seg.part"), "-vf"), StringComparison.Ordinal);
    }

    [Fact]
    public void Dv_with_bogus_average_rate_is_cfr_and_interlaced()
    {
        var v = Fixture("ffprobe-dv.json"); // avg_frame_rate 60000/1, no field_order
        Assert.Equal(new Rational(30000, 1001), v.FrameRate);
        Assert.False(v.IsVfr);
        Assert.True(v.Interlaced);
    }

    [Fact]
    public void Raw_stream_without_duration_uses_the_base_rate()
    {
        var v = Fixture("ffprobe-raw-m2v.json"); // avg_frame_rate 25/1 is a demuxer default
        Assert.Equal(new Rational(30000, 1001), v.FrameRate);
        Assert.False(v.IsVfr);
        Assert.Equal(0, v.Duration);
    }

    [Fact]
    public void Field_rate_timing_is_not_vfr()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffprobe-interlaced-vob.json"))
            .Replace("\"r_frame_rate\": \"30000/1001\"", "\"r_frame_rate\": \"60000/1001\"", StringComparison.Ordinal);
        var v = MediaProbe.ParseVideo(json);
        Assert.False(v.IsVfr);
        Assert.Equal(new Rational(30000, 1001), v.FrameRate);
    }

    [Fact]
    public void Progressive_square_pixels_get_no_deinterlace_or_setsar()
    {
        var v = Fixture("ffprobe-sample1.json");
        Assert.DoesNotContain("bwdif", ArgValue(VideoCommands.ExtractFrames("in.mp4", v), "-vf"), StringComparison.Ordinal);
        Assert.DoesNotContain("setsar", ArgValue(VideoCommands.EncodeSegment("frames", v, new AppSettings(), "seg.part"), "-vf"), StringComparison.Ordinal);
    }
}
