using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Waifu2xSimpleGui.Core.Processes;

/// <summary>
/// Pauses a job: suspends its running child processes (engine, ffmpeg) and holds new ones until
/// <see cref="Resume"/>. Flows to <see cref="ProcessRunner"/> through an async-local scope, so only
/// processes started inside <see cref="Enter"/> are affected.
/// </summary>
public sealed partial class PauseController
{
    private static readonly AsyncLocal<PauseController?> CurrentScope = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Process> _running = [];
    private TaskCompletionSource? _resumed;

    internal static PauseController? Current => CurrentScope.Value;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumed is not null;
            }
        }
    }

    /// <summary>Makes this controller apply to processes started by the calling async flow until disposed.</summary>
    public IDisposable Enter()
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = this;
        return new Scope(() => CurrentScope.Value = previous);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_resumed is not null)
            {
                return;
            }

            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var p in _running)
            {
                Signal(p, suspend: true);
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_resumed is null)
            {
                return;
            }

            foreach (var p in _running)
            {
                Signal(p, suspend: false);
            }

            _resumed.TrySetResult();
            _resumed = null;
        }
    }

    internal Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _resumed?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
    }

    internal void Register(Process process)
    {
        lock (_gate)
        {
            _running.Add(process);
            if (_resumed is not null)
            {
                Signal(process, suspend: true); // Started in the gap between the wait and Start().
            }
        }
    }

    internal void Unregister(Process process)
    {
        lock (_gate)
        {
            _running.Remove(process);
        }
    }

    private static void Signal(Process process, bool suspend)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                _ = suspend ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle);
            }
            else
            {
                // SIGSTOP/SIGCONT numbers differ between Linux and macOS/BSD.
                var signal = OperatingSystem.IsLinux() ? (suspend ? 19 : 18) : (suspend ? 17 : 19);
                _ = Kill(process.Id, signal);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited meanwhile.
        }
    }

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtResumeProcess(IntPtr processHandle);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
