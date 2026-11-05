using System.Globalization;

namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>
/// ncnn-vulkan <c>-j load:proc:save</c> thread counts. Load/save are CPU threads (decode/encode),
/// proc is per-GPU. With several GPUs the engines expect <c>load:proc,proc:save</c>.
/// </summary>
public readonly record struct ThreadConfig(int Load, int Proc, int Save)
{
    // Declared first: static initializers run in order and Default reads this.
    /// <summary>The default before it depended on the OS (settings written by v0.2.0 and earlier).</summary>
    public static ThreadConfig LegacyDefault { get; } = new(1, 2, 2);

    /// <summary>Windows: 1:2:2. Elsewhere 1:2:(half the cores, 2–8).</summary>
    public static ThreadConfig Default { get; } = ForPlatform(OperatingSystem.IsWindows(), Environment.ProcessorCount);

    // Non-Windows engine builds write PNG with stb_image_write (Windows: WIC), which is slow: measured on Linux,
    // 2 save threads at 100% CPU held an RTX 2080 Ti at ~40% GPU; 6 save threads (12-thread CPU) helped.
    internal static ThreadConfig ForPlatform(bool windows, int processors) =>
        windows ? LegacyDefault : new(1, 2, Math.Clamp(processors / 2, 2, 8));

    public string ToArgument(int gpuCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Load, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Proc, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Save, 1);
        var proc = string.Join(',', Enumerable.Repeat(Proc.ToString(CultureInfo.InvariantCulture), Math.Max(1, gpuCount)));
        return string.Create(CultureInfo.InvariantCulture, $"{Load}:{proc}:{Save}");
    }

    public static bool TryParse(string? text, out ThreadConfig config)
    {
        config = Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var load)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var proc)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var save)
            || load < 1 || proc < 1 || save < 1)
        {
            return false;
        }

        config = new ThreadConfig(load, proc, save);
        return true;
    }

    public override string ToString() => ToArgument(1);
}
