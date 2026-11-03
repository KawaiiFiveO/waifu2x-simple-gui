using System.Globalization;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Media;

public enum OutputSizeMode
{
    /// <summary>Whatever the model's scale gives.</summary>
    ModelScale,

    /// <summary>Exact factor (e.g. 1.5, 3): engine runs at the smallest model scale ≥ factor, then lanczos.</summary>
    Factor,

    /// <summary>Downscale (never upscale) so the result fits within FitWidth × FitHeight.</summary>
    Fit,
}

/// <summary>Resize step after the engine, expressed as an ffmpeg filter on the engine output.</summary>
public static class OutputSize
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Engine scale to use: in Factor mode the smallest supported scale ≥ factor (else the largest).</summary>
    public static int EngineScale(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SizeMode != OutputSizeMode.Factor)
        {
            return settings.Scale;
        }

        var scales = UpscalerCatalog.Get(settings.Engine).GetModel(settings.Model).Scales.Order().ToList();
        return scales.FirstOrDefault(s => s >= settings.TargetFactor - 1e-6, scales[^1]);
    }

    /// <summary>
    /// ffmpeg filter resizing the engine output (scaled by <paramref name="engineScale"/>), or <c>null</c> when
    /// no resize is needed. <paramref name="even"/> rounds to even sizes (yuv420p video).
    /// </summary>
    public static string? Filter(AppSettings settings, int engineScale, bool even)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (settings.SizeMode)
        {
            case OutputSizeMode.Factor:
                var ratio = settings.TargetFactor / engineScale;
                if (Math.Abs(ratio - 1) < 1e-6)
                {
                    return null;
                }

                var r = ratio.ToString("0.######", Inv);
                return even
                    ? $"scale=trunc(iw*{r}/2+0.5)*2:trunc(ih*{r}/2+0.5)*2:flags=lanczos"
                    : $"scale=max(1\\,round(iw*{r})):max(1\\,round(ih*{r})):flags=lanczos";
            case OutputSizeMode.Fit when settings.FitWidth > 0 && settings.FitHeight > 0:
                var (w, h) = (settings.FitWidth.ToString(Inv), settings.FitHeight.ToString(Inv));
                // min() keeps smaller outputs as they are; decrease keeps the aspect ratio.
                return $"scale='min(iw\\,{w})':'min(ih\\,{h})':force_original_aspect_ratio=decrease"
                    + (even ? ":force_divisible_by=2" : string.Empty) + ":flags=lanczos";
            default:
                return null;
        }
    }

    /// <summary>Re-encodes an engine output image through <paramref name="filter"/> into <paramref name="output"/> (format from extension).</summary>
    public static List<string> ResizeImage(string input, string filter, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-vf", filter, "-frames:v", "1" };
        switch (Path.GetExtension(output).ToLowerInvariant())
        {
            case ".jpg" or ".jpeg":
                args.AddRange(["-q:v", "2"]);
                break;
            case ".webp":
                args.AddRange(["-c:v", "libwebp", "-lossless", "1"]);
                break;
        }

        args.Add(output);
        return args;
    }
}
