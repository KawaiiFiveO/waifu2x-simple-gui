using System.Diagnostics;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.Core.Tests;

/// <summary>Low priority against a real ffmpeg process; skipped without ffmpeg.</summary>
public sealed class PriorityTests
{
    [Fact]
    public void Scope_flows_and_restores()
    {
        Assert.False(ProcessPriority.IsLow);
        using (ProcessPriority.EnterLow())
        {
            Assert.True(ProcessPriority.IsLow);
        }

        Assert.False(ProcessPriority.IsLow);
    }

    [Fact]
    public async Task Lower_sets_cpu_and_gpu_priority()
    {
        var ffmpeg = new ToolLocator().FindFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "ffmpeg not found");
        var psi = new ProcessStartInfo(ffmpeg!) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-nostdin", "-f", "lavfi", "-i", "testsrc2", "-t", "600", "-f", "null", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        try
        {
            ProcessPriority.LowerCpu(process);
            process.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);

            // Like ProcessRunner: GPU priority once the process has printed (graphics initialized).
            Assert.NotNull(await process.StandardError.ReadLineAsync(TestContext.Current.CancellationToken));
            Assert.True(ProcessPriority.TryLowerGpu(process));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, ProcessPriority.GetGpuPriority(process)); // BELOW_NORMAL
            }
        }
        finally
        {
            process.Kill();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }
}
