using System.Globalization;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>
/// Shared behaviour of the ncnn-vulkan CLI family (waifu2x / Real-ESRGAN / Real-CUGAN), which all
/// accept <c>-i -o -s -t -m -g -j -x -f -v</c>. Subclasses add engine-specific flags and rules.
/// </summary>
public abstract class NcnnVulkanUpscaler : IUpscaler
{
    protected NcnnVulkanUpscaler(UpscalerDescriptor descriptor, string engineDirectory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrEmpty(engineDirectory);
        Descriptor = descriptor;
        EngineDirectory = engineDirectory;
        ExecutablePath = Path.Combine(engineDirectory, ToolLocatorExtensions.WithExeSuffix(descriptor.ExecutableName));
    }

    public UpscalerDescriptor Descriptor { get; }

    public string EngineDirectory { get; }

    public string ExecutablePath { get; }

    public bool IsInstalled => File.Exists(ExecutablePath);

    public virtual string? Validate(UpscaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = Descriptor.Models.FirstOrDefault(m => m.Name == (request.Model ?? Descriptor.DefaultModel));
        if (model is null)
        {
            return $"Unknown model '{request.Model}'.";
        }

        if (!model.Scales.Contains(request.Scale))
        {
            return $"{model.DisplayName} supports scale {string.Join('/', model.Scales)}x, not {request.Scale}x.";
        }

        if (model.NoiseLevels.Count > 0 && !model.NoiseLevels.Contains(request.NoiseLevel))
        {
            return $"{model.DisplayName} supports denoise {string.Join('/', model.NoiseLevels)}, not {request.NoiseLevel}.";
        }

        return null;
    }

    public IReadOnlyList<string> BuildArguments(UpscaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gpuCount = request.GpuIds is { Count: > 0 } ids ? ids.Count : 1;
        var inv = CultureInfo.InvariantCulture;
        var model = Descriptor.GetModel(request.Model);
        var tile = request.TileSize > 0 ? request.TileSize : model.AutoTile;

        var args = new List<string>(24)
        {
            "-i", request.InputPath,
            "-o", request.OutputPath,
            "-s", request.Scale.ToString(inv),
            "-t", string.Join(',', Enumerable.Repeat(tile.ToString(inv), gpuCount)),
            "-j", request.Threads.ToArgument(gpuCount),
            "-f", request.OutputFormat.ToString().ToLowerInvariant(),
        };

        if (request.GpuIds is { Count: > 0 } gpus)
        {
            args.Add("-g");
            args.Add(string.Join(',', gpus.Select(g => g.ToString(inv))));
        }

        if (request.Tta)
        {
            args.Add("-x");
        }

        if (request.Verbose)
        {
            args.Add("-v");
        }

        AddEngineArguments(args, request, model);
        return args;
    }

    /// <summary>Adds model/denoise flags. Model paths must be absolute: engines resolve relative paths inconsistently.</summary>
    protected abstract void AddEngineArguments(List<string> args, UpscaleRequest request, ModelInfo model);

    public async Task RunAsync(UpscaleRequest request, IProgress<EngineEvent>? progress, CancellationToken cancellationToken)
    {
        var error = Validate(request);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(request));
        }

        var result = await ProcessRunner.RunAsync(
            ExecutablePath,
            BuildArguments(request),
            progress is null ? null : line => progress.Report(NcnnOutputParser.Parse(line)),
            EngineDirectory,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new EngineFailedException(Descriptor.DisplayName, result.ExitCode, result.OutputTail);
        }
    }

    public async Task<IReadOnlyList<EngineGpuDetected>> ProbeGpusAsync(CancellationToken cancellationToken)
    {
        // A missing input makes the engine enumerate devices, report "decode failed" and exit — no model load.
        var missing = Path.Combine(Path.GetTempPath(), $"w2xsg-probe-{Guid.NewGuid():N}.png");
        var gpus = new List<EngineGpuDetected>();
        var gate = new Lock();
        await ProcessRunner.RunAsync(
            ExecutablePath,
            ["-i", missing, "-o", missing + ".out.png"],
            line =>
            {
                if (NcnnOutputParser.Parse(line) is EngineGpuDetected gpu)
                {
                    lock (gate)
                    {
                        if (!gpus.Exists(g => g.Id == gpu.Id))
                        {
                            gpus.Add(gpu);
                        }
                    }
                }
            },
            EngineDirectory,
            cancellationToken).ConfigureAwait(false);

        lock (gate)
        {
            return [.. gpus.OrderBy(g => g.Id)];
        }
    }
}
