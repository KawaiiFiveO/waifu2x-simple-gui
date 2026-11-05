using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>
/// Encoded segments of one video, kept in <c>&lt;temp root&gt;/resume/&lt;key&gt;</c> across cancels and
/// crashes. The key covers the source file and every setting that changes the output, so changed
/// settings start fresh. <c>manifest.txt</c> lists "segment frames" for finished segments.
/// </summary>
internal sealed class VideoResume
{
    public const string FolderName = "resume";
    private readonly string _manifest;
    private readonly Dictionary<int, int> _segments = [];

    private VideoResume(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        _manifest = Path.Combine(directory, "manifest.txt");
        if (!File.Exists(_manifest))
        {
            return;
        }

        foreach (var line in File.ReadLines(_manifest))
        {
            var parts = line.Split(' ');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var k)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && File.Exists(SegmentPath(k)))
            {
                _segments[k] = n;
            }
        }
    }

    public string DirectoryPath { get; }

    /// <summary>Finished segment index → frame count.</summary>
    public IReadOnlyDictionary<int, int> Segments => _segments;

    public static VideoResume Open(AppSettings settings, string source, int framesPerSegment)
    {
        var file = new FileInfo(source);
        var identity = string.Join('|',
            file.FullName, file.Length, file.LastWriteTimeUtc.Ticks,
            settings.Engine, settings.Model, settings.Scale, settings.NoiseLevel, settings.Tta,
            settings.VideoEncoder, settings.VideoCrf, settings.VideoPreset, framesPerSegment,
            settings.InterpolationFactor, settings.RifeModel, settings.InterpolateOnly, settings.VfrPassthrough,
            settings.SkipsUpscale, OutputSize.Filter(settings, settings.Scale, even: true));
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return new VideoResume(Path.Combine(TempWorkspace.GetRoot(settings.TempDirectory), FolderName, key));
    }

    public string SegmentPath(int segment) =>
        Path.Combine(DirectoryPath, string.Create(CultureInfo.InvariantCulture, $"seg_{segment:D5}.mp4"));

    /// <summary>Promotes a fully written <c>.part</c> file; only then is the segment recorded.</summary>
    public void Commit(int segment, int frames, string partPath)
    {
        File.Move(partPath, SegmentPath(segment), overwrite: true);
        File.AppendAllText(_manifest, string.Create(CultureInfo.InvariantCulture, $"{segment} {frames}\n"));
        _segments[segment] = frames;
    }

    public void Delete()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort (e.g. a file still open by a scanner); stale resume folders are pruned after 7 days.
        }
    }
}
