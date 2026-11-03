using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class MediaAndSettingsTests
{
    [Theory]
    [InlineData("a.PNG", MediaKind.Image)]
    [InlineData("a.jpeg", MediaKind.Image)]
    [InlineData("a.bmp", MediaKind.Image)]
    [InlineData("a.gif", MediaKind.AnimatedGif)]
    [InlineData("a.MKV", MediaKind.Video)]
    [InlineData("a.txt", MediaKind.Unsupported)]
    [InlineData("noext", MediaKind.Unsupported)]
    public void Classifies_by_extension(string path, MediaKind kind)
    {
        Assert.Equal(kind, MediaClassifier.Classify(path));
    }

    [Fact]
    public void Finds_bundled_ffmpeg_in_bin_subfolder()
    {
        var dir = Directory.CreateTempSubdirectory("w2xsg-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(dir.FullName, "ffmpeg", "bin"));
            var probe = Path.Combine(bin.FullName, Tools.ToolLocatorExtensions.WithExeSuffix("ffprobe"));
            File.WriteAllText(probe, "");
            Assert.Equal(probe, new Tools.ToolLocator(dir.FullName).FindFfprobe());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Expand_recurses_filters_and_deduplicates()
    {
        var dir = Directory.CreateTempSubdirectory("w2xsg-");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            File.WriteAllText(Path.Combine(dir.FullName, "a.png"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "notes.txt"), "");
            File.WriteAllText(Path.Combine(sub.FullName, "b.mp4"), "");

            var flat = MediaClassifier.Expand([dir.FullName], recurse: false).ToList();
            var deep = MediaClassifier.Expand([dir.FullName, Path.Combine(dir.FullName, "a.png")], recurse: true).ToList();

            Assert.Single(flat);
            Assert.Equal(2, deep.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Settings_missing_from_an_older_file_keep_their_defaults()
    {
        var dir = Directory.CreateTempSubdirectory("w2xsg-");
        try
        {
            var store = new SettingsStore(Path.Combine(dir.FullName, "settings.json"));
            await File.WriteAllTextAsync(store.FilePath, """{ "Scale": 4 }""", TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

            var defaults = new AppSettings();
            Assert.Equal(4, loaded.Scale);
            Assert.Equal(defaults with { Scale = 4, GpuIds = loaded.GpuIds }, loaded);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Settings_round_trip_and_survive_corruption()
    {
        var dir = Directory.CreateTempSubdirectory("w2xsg-");
        try
        {
            var store = new SettingsStore(Path.Combine(dir.FullName, "cfg", "settings.json"));
            var ct = TestContext.Current.CancellationToken;

            Assert.Equal(new AppSettings().Scale, (await store.LoadAsync(ct)).Scale);

            var saved = new AppSettings { Engine = UpscalerId.Waifu2x, Scale = 4, GpuIds = [0, 1], OutputDirectory = @"D:\out" };
            await store.SaveAsync(saved, ct);
            var loaded = await store.LoadAsync(ct);
            Assert.Equal(UpscalerId.Waifu2x, loaded.Engine);
            Assert.Equal(4, loaded.Scale);
            Assert.Equal([0, 1], loaded.GpuIds);
            Assert.Equal(@"D:\out", loaded.OutputDirectory);
            Assert.Contains("\"Waifu2x\"", await File.ReadAllTextAsync(store.FilePath, ct));

            await File.WriteAllTextAsync(store.FilePath, "{ not json", ct);
            Assert.Equal(new AppSettings().Scale, (await store.LoadAsync(ct)).Scale);
            Assert.True(File.Exists(store.FilePath + ".bak"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
