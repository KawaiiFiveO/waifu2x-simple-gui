using Waifu2xSimpleGui.Core.Media;

namespace Waifu2xSimpleGui.Core.Jobs;

public enum FileState
{
    Pending,
    Processing,
    Done,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>One input file of a job. <see cref="Id"/> is chosen by the caller and echoed in updates.</summary>
public sealed record JobFile(int Id, string SourcePath, MediaKind Kind);

public abstract record JobEvent;

public sealed record FileUpdate(int FileId, FileState State, string? Detail = null, string? OutputPath = null) : JobEvent;

/// <summary>0–100 within one file (videos).</summary>
public sealed record FileProgress(int FileId, double Percent) : JobEvent;

public sealed record JobLog(string Message) : JobEvent;

/// <summary>Relative amount of work (≈ frames) for the overall progress/ETA; files without one count as 1.</summary>
public sealed record FileWeight(int FileId, double Weight) : JobEvent;
