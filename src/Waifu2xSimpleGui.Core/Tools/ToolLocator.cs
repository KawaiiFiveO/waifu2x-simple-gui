namespace Waifu2xSimpleGui.Core.Tools;

/// <summary>Finds the engines folder and ffmpeg/ffprobe.</summary>
public sealed class ToolLocator
{
    public const string EnginesDirEnvVar = "W2XSG_ENGINES_DIR";

    public ToolLocator(string? configuredEnginesDirectory = null)
    {
        EnginesRoot = ResolveEnginesRoot(configuredEnginesDirectory);
    }

    public string EnginesRoot { get; }

    /// <summary>Setting → <c>W2XSG_ENGINES_DIR</c> → <c>engines/</c> next to the executable.</summary>
    public static string ResolveEnginesRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var fromEnv = Environment.GetEnvironmentVariable(EnginesDirEnvVar);
        return !string.IsNullOrWhiteSpace(fromEnv)
            ? Path.GetFullPath(fromEnv)
            : Path.Combine(AppContext.BaseDirectory, "engines");
    }

    public string? FindFfmpeg() => FindBundledOrOnPath("ffmpeg");

    public string? FindFfprobe() => FindBundledOrOnPath("ffprobe");

    private string? FindBundledOrOnPath(string tool)
    {
        var exe = ToolLocatorExtensions.WithExeSuffix(tool);
        // ffmpeg/bin: Linux shared builds find their libraries via rpath $ORIGIN/../lib.
        foreach (var bundled in new[] { Path.Combine(EnginesRoot, "ffmpeg", exe), Path.Combine(EnginesRoot, "ffmpeg", "bin", exe) })
        {
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

public static class ToolLocatorExtensions
{
    public static string WithExeSuffix(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}
