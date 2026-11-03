namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>Output image format written by an ncnn-vulkan engine (<c>-f</c>).</summary>
public enum ImageFormat
{
    Png,
    Jpg,
    Webp,
}

/// <summary>
/// One invocation of an upscaler. <see cref="InputPath"/> and <see cref="OutputPath"/> may be a
/// single file or a directory; directories are the fast path (model loaded once, IO overlapped).
/// </summary>
public sealed record UpscaleRequest(string InputPath, string OutputPath)
{
    public int Scale { get; init; } = 2;

    /// <summary>Denoise level. -1 = none. Ignored by engines without denoise (Real-ESRGAN).</summary>
    public int NoiseLevel { get; init; }

    /// <summary>Engine-specific model name; <c>null</c> uses the engine's default.</summary>
    public string? Model { get; init; }

    /// <summary>Tile size; 0 = let the engine pick based on VRAM.</summary>
    public int TileSize { get; init; }

    /// <summary>GPU ids. <c>null</c>/empty = engine auto-select; <c>[-1]</c> = CPU.</summary>
    public IReadOnlyList<int>? GpuIds { get; init; }

    public ThreadConfig Threads { get; init; } = ThreadConfig.Default;

    public bool Tta { get; init; }

    public ImageFormat OutputFormat { get; init; } = ImageFormat.Png;

    /// <summary>Verbose output makes the engines report per-file completion, which drives progress.</summary>
    public bool Verbose { get; init; } = true;
}
