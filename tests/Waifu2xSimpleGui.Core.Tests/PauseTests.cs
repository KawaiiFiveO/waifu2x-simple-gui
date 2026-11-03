using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

/// <summary>Pause/resume against a real ffmpeg process; skipped without ffmpeg.</summary>
public sealed class PauseTests
{
    private static string Ffmpeg()
    {
        var ffmpeg = new ToolLocator().FindFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "ffmpeg not found");
        return ffmpeg!;
    }

    [Fact]
    public async Task Pause_suspends_a_running_process_and_resume_continues_it()
    {
        var ffmpeg = Ffmpeg();
        var ct = TestContext.Current.CancellationToken;
        var lines = 0;
        var pause = new PauseController();

        Task<ProcessResult> run;
        using (pause.Enter())
        {
            // A few seconds of work that prints a progress line every 0.1 s.
            run = ProcessRunner.RunAsync(
                ffmpeg,
                ["-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25", "-t", "40", "-c:v", "libx264", "-preset", "veryfast", "-stats_period", "0.1", "-f", "null", "-"],
                _ => Interlocked.Increment(ref lines),
                cancellationToken: ct);
        }

        while (Volatile.Read(ref lines) < 5)
        {
            await Task.Delay(20, ct);
        }

        pause.Pause();
        await Task.Delay(300, ct); // let already-buffered output drain
        var whilePaused = Volatile.Read(ref lines);
        await Task.Delay(1000, ct);
        Assert.InRange(Volatile.Read(ref lines), whilePaused, whilePaused + 1);
        Assert.False(run.IsCompleted);

        pause.Resume();
        Assert.Equal(0, (await run).ExitCode);
        Assert.True(lines > whilePaused + 5);
    }

    [Fact]
    public async Task Processes_started_while_paused_wait_for_resume_and_cancel_still_works()
    {
        var ffmpeg = Ffmpeg();
        var ct = TestContext.Current.CancellationToken;
        var pause = new PauseController();
        pause.Pause();

        Task<ProcessResult> run;
        using (pause.Enter())
        {
            run = ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-version"], cancellationToken: ct);
        }

        await Task.Delay(500, ct);
        Assert.False(run.IsCompleted);
        pause.Resume();
        Assert.Equal(0, (await run).ExitCode);

        // Cancelling a paused (suspended) process kills it.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ProcessResult> longRun;
        using (pause.Enter())
        {
            longRun = ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc2", "-t", "600", "-f", "null", "-"], cancellationToken: cts.Token);
        }

        await Task.Delay(300, ct);
        pause.Pause();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longRun);
    }
}
