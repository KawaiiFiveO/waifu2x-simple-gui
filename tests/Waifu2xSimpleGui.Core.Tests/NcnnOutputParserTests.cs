using Waifu2xSimpleGui.Core.Engines;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class NcnnOutputParserTests
{
    [Theory]
    [InlineData("0.00%", 0.0)]
    [InlineData("45.83%", 45.83)]
    [InlineData("100.00%", 100.0)]
    public void Parses_progress(string line, double expected)
    {
        var e = Assert.IsType<EngineProgress>(NcnnOutputParser.Parse(line));
        Assert.Equal(expected, e.Percent, 3);
    }

    [Fact]
    public void Parses_file_completed_with_spaces_in_paths()
    {
        var e = Assert.IsType<EngineFileCompleted>(NcnnOutputParser.Parse(@"C:\my files\a b.png -> D:\out dir\a b.png done"));
        Assert.Equal(@"C:\my files\a b.png", e.InputPath);
        Assert.Equal(@"D:\out dir\a b.png", e.OutputPath);
    }

    [Fact]
    public void Parses_gpu_enumeration()
    {
        var e = Assert.IsType<EngineGpuDetected>(NcnnOutputParser.Parse("[0 NVIDIA GeForce RTX 3080]  queueC=2[8]  queueG=0[16]  queueT=1[2]"));
        Assert.Equal(0, e.Id);
        Assert.Equal("NVIDIA GeForce RTX 3080", e.Name);
    }

    [Fact]
    public void Device_detail_lines_are_device_info() =>
        Assert.IsType<EngineDeviceInfo>(NcnnOutputParser.Parse("[0 NVIDIA GeForce RTX 3080]  bugsbn1=0  bugbilz=0  bugcopc=0  bugihfa=0"));

    [Theory]
    [InlineData("invalid gpu device")]
    [InlineData("both a.jpg and a.bmp output a.png ! a.jpg will output a.jpg.png")]
    public void Other_lines_are_messages(string line)
    {
        Assert.IsType<EngineMessage>(NcnnOutputParser.Parse(line));
    }

    [Theory]
    [InlineData("waifu2x-20250915-dir.txt", 4, 2, 0)]
    [InlineData("realesrgan-20220424-dir.txt", 4, 2, 34)]
    [InlineData("realcugan-20220728-dir.txt", 4, 1, 0)]
    public void Parses_real_directory_mode_output(string fixture, int done, int failed, int progress)
    {
        var events = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)).Select(NcnnOutputParser.Parse).ToList();

        Assert.Equal(done, events.OfType<EngineFileCompleted>().Count());
        Assert.Equal(failed, events.OfType<EngineFileFailed>().Count());
        Assert.Equal(progress, events.OfType<EngineProgress>().Count());
        Assert.Equal("AMD Radeon RX 9070 XT", Assert.Single(events.OfType<EngineGpuDetected>()).Name);
        Assert.All(events.OfType<EngineFileCompleted>(), e => Assert.StartsWith(@"C:\job\in/", e.InputPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Parses_decode_failure()
    {
        var e = Assert.IsType<EngineFileFailed>(NcnnOutputParser.Parse(@"decode image C:\job\in/t 1.tga failed"));
        Assert.Equal(@"C:\job\in/t 1.tga", e.InputPath);
        Assert.Equal("decode failed", e.Reason);
    }
}
