using System.Globalization;
using System.Text.RegularExpressions;

namespace Waifu2xSimpleGui.Core.Engines;

/// <summary>
/// Parses stderr of the ncnn-vulkan engines. Formats verified against waifu2x 20250915,
/// Real-ESRGAN 20220424 and Real-CUGAN 20220728 (fixtures in the tests). Notes:
/// paths are printed in the ANSI code page (non-ASCII is mangled — pipelines stage ASCII names),
/// directory mode joins with '/', and decode failures do not change the exit code.
/// </summary>
public static partial class NcnnOutputParser
{
    // "12.34%"
    [GeneratedRegex(@"^\s*(?<p>\d{1,3}(?:\.\d+)?)%\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    // "C:\in/a.png -> C:\out/a.png done"
    [GeneratedRegex(@"^(?<in>.+?) -> (?<out>.+?) done\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FileDoneRegex();

    // "decode image C:\in/a.png failed"
    [GeneratedRegex(@"^(?<op>decode|encode) image (?<path>.+) failed\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FileFailedRegex();

    // "[0 AMD Radeon RX 9070 XT]  queueC=1[8]  queueG=0[8]  queueT=2[1]" (other "[0 …]  key=…" lines are device info)
    [GeneratedRegex(@"^\[(?<id>\d+) (?<name>.+?)\]\s+(?<queue>queueC=)?", RegexOptions.CultureInvariant)]
    private static partial Regex GpuRegex();

    public static EngineEvent Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var m = ProgressRegex().Match(line);
        if (m.Success)
        {
            return new EngineProgress(double.Parse(m.Groups["p"].ValueSpan, CultureInfo.InvariantCulture));
        }

        m = FileDoneRegex().Match(line);
        if (m.Success)
        {
            return new EngineFileCompleted(m.Groups["in"].Value, m.Groups["out"].Value);
        }

        m = FileFailedRegex().Match(line);
        if (m.Success)
        {
            return new EngineFileFailed(m.Groups["path"].Value, m.Groups["op"].Value + " failed");
        }

        m = GpuRegex().Match(line);
        if (m.Success)
        {
            return m.Groups["queue"].Success
                ? new EngineGpuDetected(int.Parse(m.Groups["id"].ValueSpan, CultureInfo.InvariantCulture), m.Groups["name"].Value)
                : new EngineDeviceInfo(line);
        }

        return new EngineMessage(line);
    }
}
