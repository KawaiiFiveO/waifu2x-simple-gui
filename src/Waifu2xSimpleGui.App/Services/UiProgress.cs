using System.Collections.Concurrent;
using Avalonia.Threading;

namespace Waifu2xSimpleGui.App.Services;

/// <summary>
/// Thread-safe <see cref="IProgress{T}"/> that queues reports and applies them on the UI thread in
/// batches, at most every 100 ms — a 500-file job doesn't flood the dispatcher. A one-shot timer is
/// armed only while reports are pending (no polling).
/// </summary>
public sealed class UiProgress<T>(Action<IReadOnlyList<T>> apply) : IProgress<T>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentQueue<T> _queue = new();
    private int _armed;

    public void Report(T value)
    {
        _queue.Enqueue(value);
        if (Interlocked.Exchange(ref _armed, 1) == 0)
        {
            Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(Flush, Interval));
        }
    }

    /// <summary>Applies everything queued so far. UI thread only; call once more after the job ends.</summary>
    public void Flush()
    {
        Volatile.Write(ref _armed, 0);
        var batch = new List<T>();
        while (_queue.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count > 0)
        {
            apply(batch);
        }
    }
}
