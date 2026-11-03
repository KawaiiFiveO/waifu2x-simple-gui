using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Pipelines;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.App.Views;

/// <summary>Before/after comparison: drag (or move the mouse) to move the split.</summary>
public partial class PreviewWindow : Window
{
    private static readonly HashSet<string> Decodable = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    private readonly List<Bitmap> _bitmaps = [];
    private TempWorkspace? _workspace;

    public PreviewWindow()
    {
        InitializeComponent();
        Split.ValueChanged += (_, _) => UpdateSplit();
        Stage.SizeChanged += (_, _) => UpdateSplit();
        Stage.PointerMoved += OnPointerMoved;
        Closed += (_, _) =>
        {
            foreach (var b in _bitmaps)
            {
                b.Dispose();
            }

            _workspace?.Dispose();
        };
    }

    public static void Open(Window owner, string original, string output, MediaKind kind, AppSettings settings)
    {
        var window = new PreviewWindow { Title = $"Compare — {Path.GetFileName(original)}" };
        window.Show(owner);
        _ = window.LoadAsync(original, output, kind, settings);
    }

    private async Task LoadAsync(string original, string output, MediaKind kind, AppSettings settings)
    {
        Status.Text = "Loading…";
        try
        {
            var tools = new ToolLocator(settings.EnginesDirectory);
            var time = 0.0;
            if (kind == MediaKind.Video && tools.FindFfprobe() is { } ffprobe)
            {
                // A frame a quarter in: the first frame is often black.
                var info = await MediaProbe.ProbeVideoAsync(ffprobe, original, CancellationToken.None);
                time = info.Duration * 0.25;
            }

            var before = await LoadFrameAsync(original, time, kind, tools, settings);
            var after = await LoadFrameAsync(output, time, kind, tools, settings);
            Before.Source = before;
            After.Source = after;
            Info.Text = $"{before.PixelSize.Width}×{before.PixelSize.Height} → {after.PixelSize.Width}×{after.PixelSize.Height}"
                + (kind == MediaKind.Image ? string.Empty : $"   (frame at {time:0.#} s)");
            Status.Text = string.Empty;
            UpdateSplit();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            Status.Text = "Preview failed: " + ex.Message;
        }
    }

    private async Task<Bitmap> LoadFrameAsync(string path, double time, MediaKind kind, ToolLocator tools, AppSettings settings)
    {
        var file = path;
        if (kind != MediaKind.Image || !Decodable.Contains(Path.GetExtension(path)))
        {
            var ffmpeg = tools.FindFfmpeg() ?? throw new IOException("ffmpeg not found.");
            _workspace ??= TempWorkspace.Create(settings.TempDirectory);
            file = Path.Combine(_workspace.DirectoryPath, $"{_bitmaps.Count}.png");
            await Ffmpeg.ExtractFrameAsync(ffmpeg, path, time, file, CancellationToken.None);
        }

        var bitmap = await Task.Run(() => new Bitmap(file));
        _bitmaps.Add(bitmap);
        return bitmap;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Stage.Bounds.Width > 0)
        {
            Split.Value = Math.Clamp(e.GetPosition(Stage).X / Stage.Bounds.Width, 0, 1);
        }
    }

    // Original on the left of the split, upscaled on the right.
    private void UpdateSplit()
    {
        var (w, h) = (Stage.Bounds.Width, Stage.Bounds.Height);
        var x = w * Split.Value;
        After.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, w - x), h));
        Divider.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
    }
}
