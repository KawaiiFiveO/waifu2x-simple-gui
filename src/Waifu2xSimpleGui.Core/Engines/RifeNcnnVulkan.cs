using System.Globalization;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>A RIFE model; only v4.x supports arbitrary time steps (factors above 2).</summary>
public sealed record RifeModel(string Name, string DisplayName, bool AnyFactor);

/// <summary>
/// nihui/rife-ncnn-vulkan, directory mode: <c>-i dir -o dir -n &lt;target frames&gt;</c>. Output frame i is taken
/// at input time i·N/n (verified), so n = m·N gives input j at output m·j and repeats the last input at the end.
/// </summary>
public sealed class RifeNcnnVulkan(string engineDirectory)
{
    public const string FolderName = "rife-ncnn-vulkan";

    public static IReadOnlyList<RifeModel> Models { get; } =
    [
        new("rife-v4.6", "v4.6 (best, any factor)", true),
        new("rife-v4", "v4 (any factor)", true),
        new("rife-v3.1", "v3.1 (2x)", false),
        new("rife-v2.4", "v2.4 (2x)", false),
        new("rife-anime", "anime (2x)", false),
        new("rife-UHD", "UHD (2x)", false),
    ];

    public string EngineDirectory { get; } = engineDirectory;

    public string ExecutablePath { get; } = Path.Combine(engineDirectory, ToolLocatorExtensions.WithExeSuffix("rife-ncnn-vulkan"));

    public bool IsInstalled => File.Exists(ExecutablePath);

    public static RifeNcnnVulkan Create(string enginesRoot) => new(Path.Combine(enginesRoot, FolderName));

    /// <summary><c>null</c> when valid, otherwise a user-facing reason.</summary>
    public static string? Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var model = Models.FirstOrDefault(m => m.Name == settings.RifeModel);
        if (model is null)
        {
            return $"Unknown RIFE model '{settings.RifeModel}'.";
        }

        if (settings.InterpolationFactor is < 2 or > 8)
        {
            return "Frame interpolation factor must be 2–8.";
        }

        return settings.InterpolationFactor > 2 && !model.AnyFactor
            ? $"RIFE {model.DisplayName} only does 2x; use a v4 model for {settings.InterpolationFactor}x."
            : null;
    }

    public IReadOnlyList<string> BuildArguments(string inputDir, string outputDir, int targetFrames, int frameHeight, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var inv = CultureInfo.InvariantCulture;
        var gpuCount = settings.GpuIds.Count > 0 ? settings.GpuIds.Count : 1;
        var threads = ThreadConfig.TryParse(settings.Threads, out var t) ? t : ThreadConfig.Default;
        var args = new List<string>
        {
            "-i", inputDir,
            "-o", outputDir,
            "-n", targetFrames.ToString(inv),
            "-m", Path.Combine(EngineDirectory, settings.RifeModel),
            "-f", "%08d.png",
            "-j", threads.ToArgument(gpuCount),
            "-v",
        };
        if (settings.GpuIds.Count > 0)
        {
            args.AddRange(["-g", string.Join(',', settings.GpuIds.Select(g => g.ToString(inv)))]);
        }

        if (frameHeight >= 2160)
        {
            args.Add("-u"); // UHD mode: recommended by RIFE for 4K input.
        }

        return args;
    }

    public async Task RunAsync(IReadOnlyList<string> arguments, IProgress<EngineEvent>? progress, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            ExecutablePath,
            arguments,
            progress is null ? null : line => progress.Report(NcnnOutputParser.Parse(line)),
            EngineDirectory,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new EngineFailedException("RIFE", result.ExitCode, result.OutputTail);
        }
    }
}
