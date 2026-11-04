namespace Waifu2xSimpleGui.Core.Media;

public enum MediaKind
{
    Unsupported,
    Image,
    AnimatedGif,
    Video,
}

public static class MediaClassifier
{
    // Formats the ncnn-vulkan engines decode directly. Anything else is converted to PNG by ffmpeg first (Phase 1).
    private static readonly HashSet<string> NativeImage = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly HashSet<string> ConvertibleImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".tif", ".tiff", ".tga", ".avif", ".jxl", ".qoi", ".jp2",
    };

    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".flv", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".mts", ".m2v", ".vob", ".dv", ".3gp", ".ogv", ".asf", ".rm", ".rmvb", ".divx", ".f4v", ".mxf", ".y4m",
        ".bik", // Bink 1 (game cutscenes); Bink 2 isn't decodable by ffmpeg
    };

    /// <summary>Every supported extension (with dot), images first — for file pickers.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [.. NativeImage, .. ConvertibleImage, ".gif", .. Video];

    /// <summary>Extension-only classification (cheap; used when files are dropped).</summary>
    public static MediaKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            // Single-frame GIFs are detected at processing time and routed through the image path.
            return MediaKind.AnimatedGif;
        }

        if (NativeImage.Contains(ext) || ConvertibleImage.Contains(ext))
        {
            return MediaKind.Image;
        }

        return Video.Contains(ext) ? MediaKind.Video : MediaKind.Unsupported;
    }

    public static bool IsEngineNativeImage(string path) => NativeImage.Contains(Path.GetExtension(path));

    /// <summary>
    /// Expands dropped files/folders into supported media files, de-duplicated, in a stable order.
    /// <paramref name="skipInFolders"/> filters files found by walking folders (explicitly picked files always pass).
    /// </summary>
    public static IEnumerable<string> Expand(IEnumerable<string> paths, bool recurse, Func<string, bool>? skipInFolders = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var p in paths)
        {
            IEnumerable<string> candidates = Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => skipInFolders is null || !skipInFolders(f))
                    .Order(StringComparer.OrdinalIgnoreCase)
                : [p];

            foreach (var file in candidates)
            {
                if (Classify(file) != MediaKind.Unsupported && seen.Add(Path.GetFullPath(file)))
                {
                    yield return file;
                }
            }
        }
    }
}
