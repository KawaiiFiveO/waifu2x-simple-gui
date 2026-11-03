using CommunityToolkit.Mvvm.ComponentModel;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;

namespace Waifu2xSimpleGui.App.ViewModels;

public partial class FileItemViewModel(string path, MediaKind kind) : ViewModelBase
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    public MediaKind Kind { get; } = kind;

    public string KindLabel => Kind switch
    {
        MediaKind.Image => "Image",
        MediaKind.AnimatedGif => "GIF",
        MediaKind.Video => "Video",
        _ => "?",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessing), nameof(IsDone), nameof(ToolTip), nameof(StatusText))]
    public partial FileState State { get; set; } = FileState.Pending;

    /// <summary>Failure reason or other note from the pipeline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string? OutputPath { get; set; }

    /// <summary>0–100; only videos report it (images show an indeterminate bar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(StatusText))]
    public partial double Progress { get; set; }

    public bool HasProgress => Progress > 0;

    /// <summary>Relative work for the overall progress (≈ frames; 1 for an image).</summary>
    public double Weight { get; set; } = 1;

    public string StatusText => IsProcessing && HasProgress ? $"{State} {Progress:0}%" : State.ToString();

    public bool IsProcessing => State == FileState.Processing;

    public bool IsDone => State == FileState.Done;

    public string ToolTip => string.Join(
        Environment.NewLine,
        new[] { Path, OutputPath is null ? null : "→ " + OutputPath, Detail, IsDone ? "Double-click to compare" : null }.OfType<string>());
}
