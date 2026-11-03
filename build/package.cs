// Builds ready-to-run release packages: the app + all four engines (with models) + ffmpeg/ffprobe + licenses.
//
//   dotnet run build/package.cs -- [--rid win-x64] [--rid linux-x64] [--version 1.2.0] [--ffmpeg gpl|lgpl] [--refresh]
//
// Output: artifacts/release/Waifu2x-Simple-GUI-<version>-<rid>.zip (Windows) / .tar.gz (Linux, exec bits set).
// Downloads are cached in artifacts/cache (--refresh re-downloads). win-x64 is Native AOT. linux-x64 is Native AOT
// when built on Linux (needs clang); built on Windows it is a trimmed self-contained single file instead.
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

var rids = new List<string>();
var version = $"0.0.0-dev.{DateTime.UtcNow:yyyyMMdd}";
var flavor = "gpl";
var refresh = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rid": rids.Add(args[++i]); break;
        case "--version": version = args[++i]; break;
        case "--ffmpeg": flavor = args[++i]; break;
        case "--refresh": refresh = true; break;
        default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return 2;
    }
}

if (rids.Count == 0)
{
    rids = ["win-x64", "linux-x64"];
}

if (rids.Except(["win-x64", "linux-x64"]).FirstOrDefault() is { } bad || flavor is not ("gpl" or "lgpl"))
{
    Console.Error.WriteLine("Supported: --rid win-x64|linux-x64, --ffmpeg gpl|lgpl");
    return 2;
}

var root = FindRepoRoot();
var cache = Directory.CreateDirectory(Path.Combine(root, "artifacts", "cache")).FullName;
var outRoot = Directory.CreateDirectory(Path.Combine(root, "artifacts", "release")).FullName;
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Waifu2x-Simple-GUI-build/1.0");

foreach (var rid in rids)
{
    await PackageAsync(rid);
}

return 0;

async Task PackageAsync(string rid)
{
    var windows = rid.StartsWith("win", StringComparison.Ordinal);
    var name = $"Waifu2x-Simple-GUI-{version}-{rid}";
    var stage = Path.Combine(outRoot, name);
    if (Directory.Exists(stage))
    {
        Directory.Delete(stage, recursive: true);
    }

    Directory.CreateDirectory(stage);
    Console.WriteLine($"== {name}");

    // 1. App
    List<string> publish = ["publish", Path.Combine(root, "src", "Waifu2xSimpleGui.App"), "-c", "Release", "-r", rid, "-o", stage, $"-p:Version={version}"];
    if (!windows && !OperatingSystem.IsLinux())
    {
        // Native AOT can't cross-compile to another OS.
        publish.AddRange(["-p:PublishAot=false", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:PublishTrimmed=true"]);
        Console.WriteLine("   (linux-x64 from a non-Linux host: trimmed self-contained single file, not Native AOT)");
    }

    Run("dotnet", publish);
    foreach (var junk in Directory.EnumerateFiles(stage, "*.*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase)))
    {
        File.Delete(junk);
    }

    // 2. Engines (+ models)
    var sources = new StringBuilder($"Third-party binaries in this package ({DateTime.UtcNow:yyyy-MM-dd}). Licenses: licenses/, THIRD-PARTY.md\n\n");
    var engines = Path.Combine(stage, "engines");
    foreach (var (folder, url) in EngineAssets(windows))
    {
        var archive = await DownloadAsync(url);
        sources.Append($"engines/{folder}\n  {url}\n  sha256 {Sha256(archive)}\n");
        var extracted = Extract(archive);
        CopyEngine(SingleRoot(extracted), Path.Combine(engines, folder), folder);
        Directory.Delete(extracted, recursive: true);
    }

    // 3. ffmpeg / ffprobe (BtbN shared builds, stable release branch): the static ones duplicate every library
    //    into both executables (2 x 160 MB).
    var ffUrl = $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-{(windows ? "win64" : "linux64")}-{flavor}-shared-8.1.{(windows ? "zip" : "tar.xz")}";
    var ffArchive = await DownloadAsync(ffUrl);
    var ffExtracted = Extract(ffArchive);
    var ffRoot = SingleRoot(ffExtracted);
    // Windows: exes + DLLs side by side. Linux: bin/ + lib/ (rpath $ORIGIN/../lib), sonames only (no dev symlinks).
    var ffDest = Directory.CreateDirectory(Path.Combine(engines, "ffmpeg")).FullName;
    var ffBin = windows ? ffDest : Directory.CreateDirectory(Path.Combine(ffDest, "bin")).FullName;
    foreach (var tool in new[] { "ffmpeg", "ffprobe" })
    {
        var exe = windows ? tool + ".exe" : tool;
        File.Copy(Path.Combine(ffRoot, "bin", exe), Path.Combine(ffBin, exe));
    }

    var libs = windows
        ? Directory.EnumerateFiles(Path.Combine(ffRoot, "bin"), "*.dll")
        : Directory.EnumerateFiles(Path.Combine(ffRoot, "lib"), "*.so.*").Where(f => Path.GetFileName(f).Split('.') is [_, "so", var major] && major.All(char.IsAsciiDigit));
    var ffLib = windows ? ffDest : Directory.CreateDirectory(Path.Combine(ffDest, "lib")).FullName;
    foreach (var lib in libs)
    {
        File.Copy(lib, Path.Combine(ffLib, Path.GetFileName(lib))); // follows the soname symlink
    }

    // Text files live in docs/ so the package root only holds the app, its native libraries and engines/.
    var docs = Path.Combine(stage, "docs");
    Directory.CreateDirectory(Path.Combine(docs, "licenses"));
    File.Copy(Path.Combine(ffRoot, "LICENSE.txt"), Path.Combine(docs, "licenses", $"ffmpeg.{flavor.ToUpperInvariant()}.txt"));
    sources.Append($"engines/ffmpeg ({Path.GetFileName(ffRoot)}, {flavor.ToUpperInvariant()})\n  {ffUrl}\n  sha256 {Sha256(ffArchive)}\n"
        + "  Source: https://github.com/FFmpeg/FFmpeg (release/8.1) — build scripts and library versions: https://github.com/BtbN/FFmpeg-Builds\n");
    Directory.Delete(ffExtracted, recursive: true);

    // 4. Docs + licenses
    foreach (var license in Directory.EnumerateFiles(Path.Combine(root, "docs", "licenses")))
    {
        File.Copy(license, Path.Combine(docs, "licenses", Path.GetFileName(license)));
    }

    File.Copy(Path.Combine(root, "LICENSE.txt"), Path.Combine(docs, "LICENSE.txt"));
    File.Copy(Path.Combine(root, "README.md"), Path.Combine(docs, "README.md"));
    File.Copy(Path.Combine(root, "docs", "THIRD-PARTY.md"), Path.Combine(docs, "THIRD-PARTY.md"));
    File.WriteAllText(Path.Combine(docs, "SOURCES.txt"), sources.ToString());

    // 5. Archive
    var archivePath = Path.Combine(outRoot, name + (windows ? ".zip" : ".tar.gz"));
    File.Delete(archivePath);
    if (windows)
    {
        ZipFile.CreateFromDirectory(stage, archivePath, CompressionLevel.SmallestSize, includeBaseDirectory: true);
    }
    else
    {
        WriteTarGz(stage, name, archivePath);
    }

    Console.WriteLine($"   -> {archivePath} ({new FileInfo(archivePath).Length / 1048576.0:0.0} MB)");
}

static IEnumerable<(string Folder, string Url)> EngineAssets(bool windows)
{
    var os = windows ? "windows" : "ubuntu";
    yield return ("waifu2x-ncnn-vulkan", $"https://github.com/nihui/waifu2x-ncnn-vulkan/releases/download/20250915/waifu2x-ncnn-vulkan-20250915-{(windows ? "windows" : "linux")}.zip");
    yield return ("realesrgan-ncnn-vulkan", $"https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-{os}.zip");
    yield return ("realcugan-ncnn-vulkan", $"https://github.com/nihui/realcugan-ncnn-vulkan/releases/download/20220728/realcugan-ncnn-vulkan-20220728-{os}.zip");
    yield return ("rife-ncnn-vulkan", $"https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-{os}.zip");
}

// Sample media in the Real-ESRGAN zip is third-party content; vcomp140d.dll is a non-redistributable debug runtime.
static bool Excluded(string fileName) =>
    fileName.StartsWith("input", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
    || fileName.Equals("vcomp140d.dll", StringComparison.OrdinalIgnoreCase);

// RIFE models the app offers (keep in sync with RifeNcnnVulkan.Models); the zip has ~220 MB more.
static bool OfferedRifeModel(string dir) => dir is "rife-v4.6" or "rife-v4" or "rife-v3.1" or "rife-v2.4" or "rife-anime" or "rife-UHD";

static void CopyEngine(string from, string to, string folder)
{
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(from, file);
        var top = rel.Split(Path.DirectorySeparatorChar)[0];
        if (Excluded(Path.GetFileName(file))
            || (folder == "rife-ncnn-vulkan" && top != rel && !OfferedRifeModel(top)))
        {
            continue;
        }

        var dest = Path.Combine(to, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest);
    }
}

async Task<string> DownloadAsync(string url)
{
    var path = Path.Combine(cache, url[(url.LastIndexOf('/') + 1)..]);
    if (File.Exists(path) && !refresh)
    {
        return path;
    }

    Console.WriteLine($"   downloading {url}");
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    await using (var file = File.Create(path + ".part"))
    {
        await response.Content.CopyToAsync(file);
    }

    File.Move(path + ".part", path, overwrite: true);
    return path;
}

string Extract(string archive)
{
    var dir = Path.Combine(cache, "x-" + Path.GetRandomFileName());
    Directory.CreateDirectory(dir);
    if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        ZipFile.ExtractToDirectory(archive, dir);
    }
    else
    {
        Run("tar", ["-xf", archive, "-C", dir]); // .tar.xz: no xz support in .NET
    }

    return dir;
}

// Release archives usually wrap everything in one top-level folder.
static string SingleRoot(string dir)
{
    while (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir) is [var only])
    {
        dir = only;
    }

    return dir;
}

static void WriteTarGz(string stage, string name, string archivePath)
{
    using var gz = new GZipStream(File.Create(archivePath), CompressionLevel.SmallestSize);
    using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false);
    const UnixFileMode Dir = (UnixFileMode)0b111_101_101; // 755
    const UnixFileMode Exe = (UnixFileMode)0b111_101_101;
    const UnixFileMode Data = (UnixFileMode)0b110_100_100; // 644
    var now = DateTimeOffset.UtcNow;
    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/") { Mode = Dir, ModificationTime = now });
    foreach (var path in Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var rel = name + "/" + Path.GetRelativePath(stage, path).Replace('\\', '/');
        if (Directory.Exists(path))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, rel + "/") { Mode = Dir, ModificationTime = now });
            continue;
        }

        // Extension-less files are the ELF binaries (app, engines, ffmpeg); model .param/.bin all have extensions.
        using var data = File.OpenRead(path);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, rel)
        {
            Mode = Path.HasExtension(path) && !path.EndsWith(".so", StringComparison.Ordinal) ? Data : Exe,
            ModificationTime = now,
            DataStream = data,
        });
    }
}

static string Sha256(string file)
{
    using var stream = File.OpenRead(file);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static void Run(string exe, IEnumerable<string> arguments)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
    foreach (var a in arguments)
    {
        psi.ArgumentList.Add(a);
    }

    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        throw new InvalidOperationException($"{exe} exited with {p.ExitCode}");
    }
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Waifu2xSimpleGui.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Run from inside the repository.");
}
