namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class EmoticonBitmapLoad(string path, int decodeHeight, int generation)
{
    private readonly TaskCompletionSource<EmoticonBitmapCacheEntry?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string Path { get; } = path;
    internal int DecodeHeight { get; } = decodeHeight;
    internal int Generation { get; } = generation;
    internal CancellationTokenSource Cancellation { get; } = new();
    internal Task<EmoticonBitmapCacheEntry?> Task => completion.Task;
    internal EmoticonBitmapCacheEntry? Entry { get; set; }
    internal int Waiters { get; set; }
    internal bool IsFinished { get; set; }
    internal bool IsCacheHit { get; set; }

    internal void Complete(EmoticonBitmapCacheEntry? entry)
    {
        Entry = entry;
        entry?.Retain();
        completion.TrySetResult(entry);
    }

    internal void Cancel()
    {
        Cancellation.Cancel();
        completion.TrySetCanceled();
    }

    internal void Fail(Exception exception)
    {
        completion.TrySetException(exception);
    }

    internal void ReleaseIfUnused()
    {
        if (Waiters == 0 && IsFinished)
        {
            Entry?.ReleaseRetention();
            Entry = null;
            Cancellation.Dispose();
        }
    }
}
