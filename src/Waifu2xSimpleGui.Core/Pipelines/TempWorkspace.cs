using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>
/// Per-job temp folder (<c>&lt;temp&gt;/Waifu2x-Simple-GUI/&lt;job&gt;</c>). Holds an exclusive <c>.lock</c>
/// while alive, so <see cref="CleanupStale"/> only removes folders of dead jobs (crash, kill).
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private const string LockFile = ".lock";
    private readonly FileStream _lock;

    private TempWorkspace(string path)
    {
        DirectoryPath = path;
        Directory.CreateDirectory(path);
        _lock = new FileStream(Path.Combine(path, LockFile), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public string DirectoryPath { get; }

    public static string GetRoot(string? customTempDirectory) => Path.Combine(
        string.IsNullOrWhiteSpace(customTempDirectory) ? Path.GetTempPath() : customTempDirectory,
        SettingsStore.AppFolderName);

    public static TempWorkspace Create(string? customTempDirectory) =>
        new(Path.Combine(GetRoot(customTempDirectory), $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}"));

    public string CreateSubdirectory(string name) => Directory.CreateDirectory(Path.Combine(DirectoryPath, name)).FullName;

    public void Dispose()
    {
        _lock.Dispose();
        TryDelete(DirectoryPath);
    }

    /// <summary>Deletes job folders whose lock is not held, and video resume data older than a week. Returns the number removed.</summary>
    public static int CleanupStale(string? customTempDirectory)
    {
        var root = GetRoot(customTempDirectory);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        var resume = Path.Combine(root, VideoResume.FolderName);
        if (Directory.Exists(resume))
        {
            removed += Directory.EnumerateDirectories(resume)
                .Where(d => Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-7))
                .Count(TryDelete);
        }

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (dir == resume)
            {
                continue;
            }

            try
            {
                var lockPath = Path.Combine(dir, LockFile);
                if (File.Exists(lockPath))
                {
                    // Throws IOException while the owning job is alive.
                    new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (TryDelete(dir))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
