using Waifu2xSimpleGui.Core.Engines;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class UpscalerArgumentTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "w2xsg-engines");

    private static string ArgValue(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        Assert.True(i >= 0, $"missing {flag}");
        return args[i + 1];
    }

    [Fact]
    public void Waifu2x_builds_common_and_model_arguments()
    {
        var engine = UpscalerCatalog.Create(UpscalerId.Waifu2x, Root);
        var args = engine.BuildArguments(new UpscaleRequest("in dir", "out dir") { Scale = 2, NoiseLevel = 1 });

        Assert.Equal("in dir", ArgValue(args, "-i"));
        Assert.Equal("out dir", ArgValue(args, "-o"));
        Assert.Equal("2", ArgValue(args, "-s"));
        Assert.Equal("1", ArgValue(args, "-n"));
        Assert.Equal("0", ArgValue(args, "-t"));
        Assert.Equal("1:2:2", ArgValue(args, "-j"));
        Assert.Equal("png", ArgValue(args, "-f"));
        Assert.Equal(Path.Combine(Root, "waifu2x-ncnn-vulkan", "models-cunet"), ArgValue(args, "-m"));
        Assert.Contains("-v", args);
        Assert.DoesNotContain("-g", args);
        Assert.DoesNotContain("-x", args);
    }

    [Fact]
    public void Multi_gpu_repeats_tile_and_proc_threads_per_gpu()
    {
        var engine = UpscalerCatalog.Create(UpscalerId.RealCugan, Root);
        var args = engine.BuildArguments(new UpscaleRequest("a.png", "b.png")
        {
            GpuIds = [0, 1],
            TileSize = 256,
            Threads = new ThreadConfig(2, 3, 2),
            Tta = true,
        });

        Assert.Equal("0,1", ArgValue(args, "-g"));
        Assert.Equal("256,256", ArgValue(args, "-t"));
        Assert.Equal("2:3,3:2", ArgValue(args, "-j"));
        Assert.Contains("-x", args);
    }

    [Fact]
    public void RealEsrgan_passes_models_folder_and_model_name_without_denoise()
    {
        var engine = UpscalerCatalog.Create(UpscalerId.RealEsrgan, Root);
        var args = engine.BuildArguments(new UpscaleRequest("a.png", "b.webp") { Scale = 4, OutputFormat = ImageFormat.Webp });

        Assert.Equal(Path.Combine(Root, "realesrgan-ncnn-vulkan", "models"), ArgValue(args, "-m"));
        Assert.Equal("realesr-animevideov3", ArgValue(args, "-n"));
        Assert.Equal("webp", ArgValue(args, "-f"));
    }

    [Theory]
    [InlineData(UpscalerId.RealEsrgan, "realesrgan-x4plus", 2, 0, false)]
    [InlineData(UpscalerId.RealEsrgan, "realesrgan-x4plus", 4, 0, true)]
    [InlineData(UpscalerId.RealCugan, "models-se", 4, 1, false)]
    [InlineData(UpscalerId.RealCugan, "models-se", 4, 3, true)]
    [InlineData(UpscalerId.RealCugan, "models-se", 2, 2, true)]
    [InlineData(UpscalerId.Waifu2x, "models-cunet", 1, -1, false)]
    [InlineData(UpscalerId.Waifu2x, "models-cunet", 3, 0, false)]
    public void Validate_enforces_model_constraints(UpscalerId id, string model, int scale, int noise, bool valid)
    {
        var engine = UpscalerCatalog.Create(id, Root);
        var error = engine.Validate(new UpscaleRequest("a", "b") { Model = model, Scale = scale, NoiseLevel = noise });
        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData("1:2:2", true)]
    [InlineData("4:4:4", true)]
    [InlineData("1:0:2", false)]
    [InlineData("1:2", false)]
    [InlineData("a:b:c", false)]
    public void ThreadConfig_parses(string text, bool ok)
    {
        Assert.Equal(ok, ThreadConfig.TryParse(text, out _));
    }
}
