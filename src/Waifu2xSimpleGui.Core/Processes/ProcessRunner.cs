using System.Diagnostics;
using System.Text;

namespace Waifu2xSimpleGui.Core.Processes;

public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> OutputTail);

/// <summary>
/// Event-driven child-process runner: no polling, lines are pushed as they arrive, cancellation
/// kills the whole process tree. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
/// so paths with spaces/unicode never need manual quoting.
/// </summary>
public static class ProcessRunner
{
    private const int TailLines = 40;

    public static Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onLine = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(executable, arguments, onLine, null, workingDirectory, cancellationToken);

    /// <summary>Like <see cref="RunAsync"/>, but stdout is captured separately (e.g. ffprobe JSON); stderr feeds the tail.</summary>
    public static async Task<(ProcessResult Result, string Stdout)> CaptureAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var result = await RunCoreAsync(executable, arguments, null, stdout, null, cancellationToken).ConfigureAwait(false);
        lock (stdout)
        {
            return (result, stdout.ToString());
        }
    }

    private static async Task<ProcessResult> RunCoreAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        StringBuilder? stdout,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? string.Empty,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var tail = new Queue<string>(TailLines);
        var gate = new Lock();

        void OnData(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                if (tail.Count == TailLines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(e.Data);
            }

            onLine?.Invoke(e.Data);
        }

        void OnStdout(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (stdout!)
                {
                    stdout.Append(e.Data).Append('\n');
                }
            }
        }

        // A paused job starts nothing new until resumed.
        var pause = PauseController.Current;
        if (pause is not null)
        {
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += stdout is null ? OnData : OnStdout;
        process.ErrorDataReceived += OnData;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }

        pause?.Register(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Also waits for both redirected streams to reach EOF.
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pause?.Unregister(process);
            TryKill(process);
            // Kill is asynchronous: wait until the process is really gone, otherwise its writer
            // threads can still create files while the caller deletes the temp folder.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Unkillable (e.g. stuck driver call); give up rather than hang the app.
            }

            throw;
        }

        pause?.Unregister(process);
        lock (gate)
        {
            return new ProcessResult(process.ExitCode, [.. tail]);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied / exiting; nothing more we can do.
        }
    }
}
