using System.Globalization;

namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>nihui/waifu2x-ncnn-vulkan. <c>-m</c> is a model folder; denoise via <c>-n</c>.</summary>
public sealed class Waifu2xNcnnVulkan(string engineDirectory) : NcnnVulkanUpscaler(Definition, engineDirectory)
{
    private static readonly int[] Scales = [1, 2, 4, 8, 16, 32];
    private static readonly int[] Noise = [-1, 0, 1, 2, 3];

    public static UpscalerDescriptor Definition { get; } = new(
        UpscalerId.Waifu2x,
        "waifu2x (ncnn-vulkan)",
        FolderName: "waifu2x-ncnn-vulkan",
        ExecutableName: "waifu2x-ncnn-vulkan",
        Models:
        [
            new("models-cunet", "CUnet (best for anime art)", Scales, Noise),
            new("models-upconv_7_anime_style_art_rgb", "UpConv7 anime (fast)", Scales, Noise),
            new("models-upconv_7_photo", "UpConv7 photo", Scales, Noise),
        ],
        DefaultModel: "models-cunet");

    public override string? Validate(UpscaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scale == 1 && request.NoiseLevel == -1)
        {
            return "Scale 1x with no denoise does nothing.";
        }

        return base.Validate(request);
    }

    protected override void AddEngineArguments(List<string> args, UpscaleRequest request, ModelInfo model)
    {
        args.Add("-n");
        args.Add(request.NoiseLevel.ToString(CultureInfo.InvariantCulture));
        args.Add("-m");
        args.Add(Path.Combine(EngineDirectory, model.Name));
    }
}

/// <summary>xinntao/Real-ESRGAN-ncnn-vulkan. <c>-m</c> is the models folder, <c>-n</c> the model name. No denoise.</summary>
public sealed class RealEsrganNcnnVulkan(string engineDirectory) : NcnnVulkanUpscaler(Definition, engineDirectory)
{
    public static UpscalerDescriptor Definition { get; } = new(
        UpscalerId.RealEsrgan,
        "Real-ESRGAN (ncnn-vulkan)",
        FolderName: "realesrgan-ncnn-vulkan",
        ExecutableName: "realesrgan-ncnn-vulkan",
        Models:
        [
            new("realesr-animevideov3", "AnimeVideo v3 (fast, anime video)", [2, 3, 4], []),
            new("realesrgan-x4plus-anime", "x4plus anime (anime images)", [4], []),
            new("realesrgan-x4plus", "x4plus (photos / general)", [4], []),
        ],
        DefaultModel: "realesr-animevideov3");

    protected override void AddEngineArguments(List<string> args, UpscaleRequest request, ModelInfo model)
    {
        args.Add("-m");
        args.Add(Path.Combine(EngineDirectory, "models"));
        args.Add("-n");
        args.Add(model.Name);
    }
}

/// <summary>
/// nihui/realcugan-ncnn-vulkan. <c>-m</c> is a model folder; denoise via <c>-n</c>.
/// Allowed denoise levels depend on model *and* scale (se: 2x → -1..3, 3x/4x → -1/0/3).
/// </summary>
public sealed class RealCuganNcnnVulkan(string engineDirectory) : NcnnVulkanUpscaler(Definition, engineDirectory)
{
    public static UpscalerDescriptor Definition { get; } = new(
        UpscalerId.RealCugan,
        "Real-CUGAN (ncnn-vulkan)",
        FolderName: "realcugan-ncnn-vulkan",
        ExecutableName: "realcugan-ncnn-vulkan",
        Models:
        [
            new("models-se", "SE (standard)", [2, 3, 4], [-1, 0, 1, 2, 3]),
            new("models-pro", "Pro", [2, 3], [-1, 0, 3]),
        ],
        DefaultModel: "models-se");

    public override string? Validate(UpscaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.Model ?? Descriptor.DefaultModel) == "models-se" && request.Scale > 2 && request.NoiseLevel is 1 or 2)
        {
            return "Real-CUGAN SE supports denoise 1 and 2 only at 2x.";
        }

        return base.Validate(request);
    }

    protected override void AddEngineArguments(List<string> args, UpscaleRequest request, ModelInfo model)
    {
        args.Add("-n");
        args.Add(request.NoiseLevel.ToString(CultureInfo.InvariantCulture));
        args.Add("-m");
        args.Add(Path.Combine(EngineDirectory, model.Name));
    }
}

public static class UpscalerCatalog
{
    public static IReadOnlyList<UpscalerDescriptor> Descriptors { get; } =
    [
        Waifu2xNcnnVulkan.Definition,
        RealEsrganNcnnVulkan.Definition,
        RealCuganNcnnVulkan.Definition,
    ];

    public static UpscalerDescriptor Get(UpscalerId id) => Descriptors.First(d => d.Id == id);

    public static IUpscaler Create(UpscalerId id, string enginesRoot)
    {
        var dir = Path.Combine(enginesRoot, Get(id).FolderName);
        return id switch
        {
            UpscalerId.Waifu2x => new Waifu2xNcnnVulkan(dir),
            UpscalerId.RealEsrgan => new RealEsrganNcnnVulkan(dir),
            UpscalerId.RealCugan => new RealCuganNcnnVulkan(dir),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
        };
    }
}
