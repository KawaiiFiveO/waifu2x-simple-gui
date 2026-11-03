namespace Waifu2xSimpleGui.Core.Engines;

public enum UpscalerId
{
    Waifu2x,
    RealEsrgan,
    RealCugan,
}

/// <summary>A selectable model and the settings it accepts.</summary>
/// <param name="Name">Value passed to the engine (folder name or <c>-n</c> model name).</param>
/// <param name="DisplayName">Label shown in the UI.</param>
/// <param name="Scales">Supported upscale ratios.</param>
/// <param name="NoiseLevels">Supported denoise levels; empty when the model has no denoise option.</param>
public sealed record ModelInfo(string Name, string DisplayName, IReadOnlyList<int> Scales, IReadOnlyList<int> NoiseLevels);

/// <summary>Static, path-independent description of an engine (used by the UI before any engine is located).</summary>
public sealed record UpscalerDescriptor(
    UpscalerId Id,
    string DisplayName,
    string FolderName,
    string ExecutableName,
    IReadOnlyList<ModelInfo> Models,
    string DefaultModel)
{
    public ModelInfo GetModel(string? name) =>
        Models.FirstOrDefault(m => m.Name == (name ?? DefaultModel))
        ?? throw new ArgumentException($"Unknown model '{name}' for {DisplayName}.", nameof(name));
}
