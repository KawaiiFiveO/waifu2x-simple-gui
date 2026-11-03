namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>Something an engine reported on stdout/stderr.</summary>
public abstract record EngineEvent;

/// <summary>Tile progress within the current image, 0–100. Only Real-ESRGAN prints this.</summary>
public sealed record EngineProgress(double Percent) : EngineEvent;

/// <summary>One file finished (reported in verbose mode as <c>in -&gt; out done</c>).</summary>
public sealed record EngineFileCompleted(string InputPath, string OutputPath) : EngineEvent;

/// <summary>The engine could not decode or encode a file (<c>decode image X failed</c>). The process still exits 0.</summary>
public sealed record EngineFileFailed(string InputPath, string Reason) : EngineEvent;

/// <summary>A Vulkan device the engine enumerated at startup.</summary>
public sealed record EngineGpuDetected(int Id, string Name) : EngineEvent;

/// <summary>Other per-device startup lines (<c>[0 GPU]  fp16-p/s/a=…</c>) — noise for users.</summary>
public sealed record EngineDeviceInfo(string Text) : EngineEvent;

/// <summary>Any other line (warnings, errors, info) — surfaced in the log.</summary>
public sealed record EngineMessage(string Text) : EngineEvent;

public sealed class EngineFailedException : Exception
{
    public EngineFailedException(string engine, int exitCode, IReadOnlyList<string> outputTail)
        : base($"{engine} exited with code {exitCode}.{Environment.NewLine}{string.Join(Environment.NewLine, outputTail)}")
    {
        Engine = engine;
        ExitCode = exitCode;
        OutputTail = outputTail;
    }

    public string Engine { get; }

    public int ExitCode { get; }

    public IReadOnlyList<string> OutputTail { get; }
}
