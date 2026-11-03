namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>
/// An external super-resolution engine. Implementations only translate a request into a process
/// invocation and translate the process output into <see cref="EngineEvent"/>s; batching, staging
/// and media handling live in the pipelines.
/// </summary>
public interface IUpscaler
{
    UpscalerDescriptor Descriptor { get; }

    /// <summary>Full path of the engine executable.</summary>
    string ExecutablePath { get; }

    bool IsInstalled { get; }

    /// <summary>Returns <c>null</c> when the request is valid for this engine, otherwise a user-facing reason.</summary>
    string? Validate(UpscaleRequest request);

    IReadOnlyList<string> BuildArguments(UpscaleRequest request);

    /// <summary>Runs the engine to completion. Throws <see cref="EngineFailedException"/> on a non-zero exit.</summary>
    Task RunAsync(UpscaleRequest request, IProgress<EngineEvent>? progress, CancellationToken cancellationToken);

    /// <summary>Lists the Vulkan devices the engine sees (empty when none).</summary>
    Task<IReadOnlyList<EngineGpuDetected>> ProbeGpusAsync(CancellationToken cancellationToken);
}
