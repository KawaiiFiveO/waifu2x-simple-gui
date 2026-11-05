using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>
/// Final output paths: <see cref="AppSettings.OutputDirectory"/> (or the source's folder) +
/// <see cref="AppSettings.FileNameTemplate"/>. Paths are reserved so two sources never map to the
/// same output within a job, and a source file is never overwritten.
/// </summary>
public sealed class OutputNaming(AppSettings settings)
{
    /// <summary>{engine} when <see cref="AppSettings.SkipsUpscale"/>.</summary>
    public const string ResizeToken = "resize";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly HashSet<string> _reserved = new(PathComparer);

    /// <param name="extension">Including the dot, e.g. <c>.png</c>.</param>
    /// <param name="engineToken">Overrides {engine} (e.g. "rife" for interpolate-only video).</param>
    /// <param name="scaleToken">Overrides {scale}.</param>
    public string Reserve(string sourcePath, string extension, string? engineToken = null, int? scaleToken = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var dir = settings.OutputDirectory is { Length: > 0 } o ? Path.GetFullPath(o) : Path.GetDirectoryName(source)!;
        var stem = ExpandTemplate(settings, source, engineToken, scaleToken);

        var candidate = Path.Combine(dir, stem + extension);
        for (var n = 1; IsTaken(candidate, source); n++)
        {
            candidate = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}"));
        }

        _reserved.Add(candidate);
        return candidate;
    }

    private bool IsTaken(string candidate, string source) =>
        _reserved.Contains(candidate)
        || PathComparer.Equals(candidate, source)
        || (!settings.OverwriteExisting && Path.Exists(candidate));

    /// <summary>
    /// Matches file names <paramref name="template"/> produces for any engine/scale/noise (incl. " (n)"
    /// de-dup suffixes), so re-adding a folder skips earlier outputs. <c>null</c> when the template
    /// can't be told apart from a source name (plain <c>{name}</c>).
    /// <paramref name="resizeOnly"/>: only resize outputs count (upscaled files are the inputs), so the
    /// template needs {engine} to tell them apart.
    /// </summary>
    public static Regex? OutputPattern(string? template, bool resizeOnly = false)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Trim().Equals("{name}", StringComparison.OrdinalIgnoreCase)
            || (resizeOnly && !template.Contains("{engine}", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var engines = resizeOnly
            ? ResizeToken
            : string.Join('|', UpscalerCatalog.Descriptors.Select(d => d.Id.ToString().ToLowerInvariant()).Append("rife").Append(ResizeToken));
        var body = new StringBuilder("^");
        foreach (var part in Regex.Split(template.Trim(), @"(\{(?:name|engine|scale|size|noise)\})", RegexOptions.IgnoreCase))
        {
            body.Append(part.ToLowerInvariant() switch
            {
                "{name}" => ".+",
                "{engine}" => $"(?:{engines})",
                "{scale}" => @"\d+(?:\.\d+)?",
                "{size}" => @"\d+(?:\.\d+)?x(?:\d+)?",
                "{noise}" => @"-?\d+",
                _ => Regex.Escape(part),
            });
        }

        body.Append(@"(?: \(\d+\))?\.[^.]+$");
        return new Regex(body.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>{scale}: the target factor in Factor mode (e.g. "1.5"), otherwise the engine scale.</summary>
    private static string ScaleLabel(AppSettings settings) => settings.SizeMode == OutputSizeMode.Factor
        ? settings.TargetFactor.ToString("0.##", CultureInfo.InvariantCulture)
        : settings.Scale.ToString(CultureInfo.InvariantCulture);

    /// <summary>{size}: the Fit box ("1920x1080"), otherwise the scale with an x ("0.5x").</summary>
    private static string SizeLabel(AppSettings settings, int? scaleToken) =>
        settings.SizeMode == OutputSizeMode.Fit && scaleToken is null
            ? string.Create(CultureInfo.InvariantCulture, $"{settings.FitWidth}x{settings.FitHeight}")
            : (scaleToken?.ToString(CultureInfo.InvariantCulture) ?? ScaleLabel(settings)) + "x";

    /// <summary>Tokens: {name} {engine} {scale} {size} {noise}. Invalid file-name characters become '_'.</summary>
    public static string ExpandTemplate(AppSettings settings, string sourcePath, string? engineToken = null, int? scaleToken = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var inv = CultureInfo.InvariantCulture;
        var template = string.IsNullOrWhiteSpace(settings.FileNameTemplate) ? "{name}" : settings.FileNameTemplate;
        var name = template
            .Replace("{name}", Path.GetFileNameWithoutExtension(sourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{engine}", engineToken ?? (settings.SkipsUpscale ? ResizeToken : settings.Engine.ToString().ToLowerInvariant()), StringComparison.OrdinalIgnoreCase)
            .Replace("{scale}", scaleToken?.ToString(inv) ?? ScaleLabel(settings), StringComparison.OrdinalIgnoreCase)
            .Replace("{size}", SizeLabel(settings, scaleToken), StringComparison.OrdinalIgnoreCase)
            .Replace("{noise}", settings.NoiseLevel.ToString(inv), StringComparison.OrdinalIgnoreCase);

        return string.Create(name.Length, name, static (span, n) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = InvalidNameChars.Contains(n[i]) ? '_' : n[i];
            }
        }).Trim();
    }

    // Windows' set on every OS (Linux only forbids '/' and NUL): outputs often end up on shared or Windows drives.
    private static readonly System.Buffers.SearchValues<char> InvalidNameChars = System.Buffers.SearchValues.Create(
        [.. Path.GetInvalidFileNameChars(), .. @"<>:""/\|?*", .. Enumerable.Range(0, 32).Select(c => (char)c)]);
}
