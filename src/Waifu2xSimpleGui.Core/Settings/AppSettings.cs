using System.Text.Json.Serialization;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Media;

namespace Waifu2xSimpleGui.Core.Settings;

/// <summary>
/// Persisted user settings. Immutable: the UI edits a copy and a job snapshots the instance it was
/// started with, so workers never read live UI state.
/// </summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;

    // ---- Engine ----
    public UpscalerId Engine { get; init; } = UpscalerId.Waifu2x;
    public string? Model { get; init; }
    public int Scale { get; init; } = 2;
    public OutputSizeMode SizeMode { get; init; } = OutputSizeMode.ModelScale;
    /// <summary>Factor mode: final size relative to the source.</summary>
    public double TargetFactor { get; init; } = 3;
    /// <summary>Fit mode: bounding box (downscale only).</summary>
    public int FitWidth { get; init; } = 3840;
    public int FitHeight { get; init; } = 2160;
    public int NoiseLevel { get; init; } = -1;
    public int TileSize { get; init; }
    /// <summary>Empty = auto. Several ids = multi-GPU (engine-native).</summary>
    public IReadOnlyList<int> GpuIds { get; init; } = [];
    public string Threads { get; init; } = ThreadConfig.Default.ToString();
    public bool Tta { get; init; }

    // ---- Output ----
    /// <summary><c>null</c> = next to each source file.</summary>
    public string? OutputDirectory { get; init; }
    /// <summary>Tokens: {name} {engine} {scale} {noise}.</summary>
    public string FileNameTemplate { get; init; } = "{name}_{engine}_{scale}x";
    public ImageFormat ImageFormat { get; init; } = ImageFormat.Png;
    public bool OverwriteExisting { get; init; }
    public bool ScanSubfolders { get; init; } = true;
    /// <summary>What animated GIFs become.</summary>
    public AnimationFormat GifOutput { get; init; } = AnimationFormat.Gif;

    // ---- Video ----
    public string VideoEncoder { get; init; } = "libx264";
    public int VideoCrf { get; init; } = 18;
    public string VideoPreset { get; init; } = "medium";
    /// <summary>Frames per segment; bounds temp disk usage and enables stage overlap.</summary>
    public int VideoSegmentFrames { get; init; } = 600;
    /// <summary>Video frame-rate multiplier via RIFE; 1 = off.</summary>
    public int InterpolationFactor { get; init; } = 1;
    public string RifeModel { get; init; } = "rife-v4.6";
    /// <summary>Videos: interpolate without upscaling.</summary>
    public bool InterpolateOnly { get; init; }
    /// <summary>Keep variable-frame-rate timing (phones, screen captures) instead of converting to constant fps.</summary>
    public bool VfrPassthrough { get; init; }

    // ---- UI ----
    public bool LightTheme { get; init; }

    // ---- Paths ----
    /// <summary><c>null</c> = default lookup (see ToolLocator).</summary>
    public string? EnginesDirectory { get; init; }
    /// <summary><c>null</c> = system temp. A fast local drive helps with video.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>Engine request for these settings (invalid thread strings fall back to defaults).</summary>
    public UpscaleRequest ToRequest(string inputPath, string outputPath) => new(inputPath, outputPath)
    {
        Scale = Scale,
        NoiseLevel = NoiseLevel,
        Model = Model,
        TileSize = TileSize,
        GpuIds = GpuIds,
        Threads = ThreadConfig.TryParse(Threads, out var t) ? t : ThreadConfig.Default,
        Tta = Tta,
        OutputFormat = ImageFormat,
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
