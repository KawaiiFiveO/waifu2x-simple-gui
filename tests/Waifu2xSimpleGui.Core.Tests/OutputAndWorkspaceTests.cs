using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Pipelines;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class OutputAndWorkspaceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("w2xsg-out-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Template_expands_tokens_and_sanitizes()
    {
        var s = new AppSettings { Engine = UpscalerId.Waifu2x, Scale = 4, NoiseLevel = 1, FileNameTemplate = "{name}-{engine}-{scale}x-n{noise}:" };
        Assert.Equal("cat-waifu2x-4x-n1_", OutputNaming.ExpandTemplate(s, Path.Combine("d", "cat.jpg")));
    }

    [Theory]
    [InlineData("pic_waifu2x_2x.png", true)]
    [InlineData("pic_realcugan_4x (3).jpg", true)]
    [InlineData("clip ü_realesrgan_2x.mp4", true)]
    [InlineData("pic.png", false)]
    [InlineData("pic_waifu2x_2x", false)]
    [InlineData("pic_other_2x.png", false)]
    public void Output_pattern_matches_earlier_outputs(string file, bool match) =>
        Assert.Equal(match, OutputNaming.OutputPattern("{name}_{engine}_{scale}x")!.IsMatch(file));

    [Fact]
    public void Plain_name_template_has_no_output_pattern() => Assert.Null(OutputNaming.OutputPattern(" {name} "));

    [Fact]
    public void Expand_skips_earlier_outputs_only_inside_folders()
    {
        var src = Path.Combine(_dir.FullName, "a.png");
        var output = Path.Combine(_dir.FullName, "a_waifu2x_2x.png");
        File.WriteAllText(src, "x");
        File.WriteAllText(output, "x");
        var pattern = OutputNaming.OutputPattern(new AppSettings().FileNameTemplate)!;
        bool Skip(string f) => pattern.IsMatch(Path.GetFileName(f));

        Assert.Equal([src], MediaClassifier.Expand([_dir.FullName], recurse: false, Skip));
        Assert.Equal([output], MediaClassifier.Expand([output], recurse: false, Skip));
    }

    [Fact]
    public void Default_engine_is_waifu2x() => Assert.Equal(UpscalerId.Waifu2x, new AppSettings().Engine);

    [Fact]
    public void Reserve_never_overwrites_source_and_respects_overwrite_flag()
    {
        var src = Path.Combine(_dir.FullName, "a.png");
        File.WriteAllText(src, "x");
        File.WriteAllText(Path.Combine(_dir.FullName, "a_up.png"), "x");

        var same = new OutputNaming(new AppSettings { FileNameTemplate = "{name}", OverwriteExisting = true });
        Assert.Equal(Path.Combine(_dir.FullName, "a (1).png"), same.Reserve(src, ".png"));

        var keep = new OutputNaming(new AppSettings { FileNameTemplate = "{name}_up" });
        Assert.Equal(Path.Combine(_dir.FullName, "a_up (1).png"), keep.Reserve(src, ".png"));

        var overwrite = new OutputNaming(new AppSettings { FileNameTemplate = "{name}_up", OverwriteExisting = true });
        Assert.Equal(Path.Combine(_dir.FullName, "a_up.png"), overwrite.Reserve(src, ".png"));
        Assert.Equal(Path.Combine(_dir.FullName, "a_up (1).png"), overwrite.Reserve(src, ".png"));
    }

    [Fact]
    public void Cleanup_removes_only_dead_workspaces()
    {
        var root = TempWorkspace.GetRoot(_dir.FullName);
        var stale = Directory.CreateDirectory(Path.Combine(root, "stale")).FullName;
        File.WriteAllText(Path.Combine(stale, "frame.png"), "x");

        using var live = TempWorkspace.Create(_dir.FullName);
        Assert.Equal(1, TempWorkspace.CleanupStale(_dir.FullName));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(live.DirectoryPath));

        live.Dispose();
        Assert.False(Directory.Exists(live.DirectoryPath));
    }

    [Theory]
    [InlineData(@"C:\job\in/000012.png", "000012")]
    [InlineData("/tmp/job/in/000003.jpg", "000003")]
    [InlineData(@"C:\job\in\000007.webp", "000007")]
    public void Stem_of_engine_path(string path, string stem) => Assert.Equal(stem, ImagePipeline.StemOf(path));
}
