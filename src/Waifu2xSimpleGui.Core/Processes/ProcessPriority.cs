using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Waifu2xSimpleGui.Core.Processes;

/// <summary>
/// Low priority for a job's processes: below-normal CPU priority (nice 10 on Unix) and, on Windows,
/// below-normal GPU scheduling priority, so other apps and the desktop get the GPU first. Flows to
/// <see cref="ProcessRunner"/> through an async-local scope like <see cref="PauseController"/>.
/// </summary>
public static partial class ProcessPriority
{
    // D3DKMT_SCHEDULINGPRIORITYCLASS_BELOW_NORMAL
    private const int GpuBelowNormal = 1;

    private static readonly AsyncLocal<bool> LowScope = new();

    internal static bool IsLow => LowScope.Value;

    /// <summary>Processes started by the calling async flow run at low priority until disposed.</summary>
    public static IDisposable EnterLow()
    {
        var previous = LowScope.Value;
        LowScope.Value = true;
        return new Scope(() => LowScope.Value = previous);
    }

    internal static void LowerCpu(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Exited meanwhile, or not permitted: run at normal priority.
        }
    }

    /// <summary>
    /// Windows GPU priority. Fails (STATUS_INVALID_PARAMETER) until the process has initialized graphics,
    /// and isn't inherited, so <see cref="ProcessRunner"/> retries on each output line — engines print
    /// their GPU line right after Vulkan init. Returns true once set (or not applicable).
    /// </summary>
    internal static bool TryLowerGpu(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return D3DKMTSetProcessSchedulingPriorityClass(process.Handle, GpuBelowNormal) == 0;
        }
        catch (InvalidOperationException)
        {
            return true; // Exited.
        }
    }

    /// <summary>Windows GPU scheduling priority class of <paramref name="process"/> (tests).</summary>
    internal static int GetGpuPriority(Process process) =>
        D3DKMTGetProcessSchedulingPriorityClass(process.Handle, out var priority) == 0 ? priority : -1;

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
