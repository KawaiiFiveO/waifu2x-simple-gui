using System.Globalization;

namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>
/// ncnn-vulkan <c>-j load:proc:save</c> thread counts. Load/save are CPU threads (decode/encode),
/// proc is per-GPU. With several GPUs the engines expect <c>load:proc,proc:save</c>.
/// </summary>
public readonly record struct ThreadConfig(int Load, int Proc, int Save)
{
    public static ThreadConfig Default { get; } = new(1, 2, 2);

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
