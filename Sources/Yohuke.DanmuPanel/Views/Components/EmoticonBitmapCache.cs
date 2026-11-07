using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class EmoticonBitmapCache
{
    private const int DEFAULT_MAX_ENTRIES = 128;
    private const long DEFAULT_MAX_RETAINED_BYTES = 16 * 1024 * 1024;
    private const int MAX_CONCURRENT_DECODES = 2;
    private const int MAX_QUEUED_DECODES = 64;
    private static readonly TimeSpan VALIDATION_INTERVAL = TimeSpan.FromSeconds(1);
    private readonly int maxEntries;
    private readonly long maxRetainedBytes;
    private readonly Dictionary<(string Path, int DecodeHeight), LinkedListNode<EmoticonBitmapCacheEntry>> entries = [];
    private readonly LinkedList<EmoticonBitmapCacheEntry> recency = [];
    private readonly Dictionary<(string Path, int DecodeHeight), EmoticonBitmapLoad> pending = [];
    private readonly SemaphoreSlim decodeSlots = new(MAX_CONCURRENT_DECODES);
    private readonly SemaphoreSlim queueSlots = new(MAX_QUEUED_DECODES);
    private long decodeCount;
    private int generation;

    internal int Count => entries.Count;
    internal long RetainedBytes { get; private set; }
    internal long DecodeCount => Interlocked.Read(ref decodeCount);
    internal long HitCount { get; private set; }
    internal int LastDecodeThreadId { get; private set; }
    internal int PendingCount => pending.Count;
    internal event Action<string, int>? ImageChanged;

    internal EmoticonBitmapCache(int maxEntries = DEFAULT_MAX_ENTRIES,
        long maxRetainedBytes = DEFAULT_MAX_RETAINED_BYTES)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedBytes);
        this.maxEntries = maxEntries;
        this.maxRetainedBytes = maxRetainedBytes;
    }

    internal EmoticonBitmapLease? TryAcquire(string path, int decodeHeight)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!entries.TryGetValue((path, decodeHeight), out var cached))
        {
            return null;
        }
        recency.Remove(cached);
        recency.AddFirst(cached);
        HitCount++;
        var lease = cached.Value.AcquireLease();
        if (Stopwatch.GetElapsedTime(cached.Value.LastCheckedAt) >= VALIDATION_INTERVAL)
        {
            cached.Value.LastCheckedAt = Stopwatch.GetTimestamp();
            _ = ValidateAsync(path, decodeHeight);
        }
        return lease;
    }

    internal async Task<EmoticonBitmapLease?> AcquireAsync(string path, int decodeHeight,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeHeight);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (path, decodeHeight);
        if (!pending.TryGetValue(key, out var request))
        {
            request = new(path, decodeHeight, generation);
            pending.Add(key, request);
            request.Waiters++;
            _ = LoadAsync(request);
        }
        else
        {
            request.Waiters++;
        }
        try
        {
            var entry = await request.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Generation != generation)
            {
                throw new OperationCanceledException();
            }
            if (request.IsCacheHit)
            {
                HitCount++;
            }
            return entry?.AcquireLease();
        }
        finally
        {
            request.Waiters--;
            if (request.Waiters == 0)
            {
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                {
                    pending.Remove(key);
                }
                if (!request.IsFinished)
                {
                    request.Cancel();
                }
                request.ReleaseIfUnused();
            }
        }
    }

    private async Task LoadAsync(EmoticonBitmapLoad request)
    {
        var key = (request.Path, request.DecodeHeight);
        var cancellationToken = request.Cancellation.Token;
        entries.TryGetValue(key, out var original);
        using var originalLease = original?.Value.AcquireLease();
        EmoticonBitmapLoadResult? result = null;
        try
        {
            await queueSlots.WaitAsync(cancellationToken);
            try
            {
                await decodeSlots.WaitAsync(cancellationToken);
            }
            finally
            {
                queueSlots.Release();
            }
            try
            {
                result = await Task.Run(() => Decode(request.Path, request.DecodeHeight, original?.Value,
                    cancellationToken), cancellationToken);
            }
            finally
            {
                decodeSlots.Release();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Generation != generation || !pending.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, request))
            {
                request.Cancel();
                return;
            }
            EmoticonBitmapCacheEntry? entry = null;
            if (result is { Image: null } && original is not null)
            {
                entry = original.Value;
                entry.LastCheckedAt = Stopwatch.GetTimestamp();
                if (ReferenceEquals(original.List, recency))
                {
                    recency.Remove(original);
                    recency.AddFirst(original);
                }
                request.IsCacheHit = true;
            }
            else
            {
                if (original is not null && ReferenceEquals(original.List, recency))
                {
                    Remove(original);
                }
                if (result?.Image is { } image)
                {
                    entry = new(request.Path, request.DecodeHeight, result.FileLength, result.LastWrittenAt, image)
                    {
                        LastCheckedAt = Stopwatch.GetTimestamp()
                    };
                    result = null;
                    request.Complete(entry);
                    if (maxEntries > 0 && entry.ByteSize <= maxRetainedBytes)
                    {
                        entry.Retain();
                        entries.Add(key, recency.AddFirst(entry));
                        RetainedBytes += entry.ByteSize;
                        while (entries.Count > maxEntries || RetainedBytes > maxRetainedBytes)
                        {
                            Remove(recency.Last!);
                        }
                    }
                }
            }
            if (request.Entry is null)
            {
                request.Complete(entry);
            }
            if (original is not null && !request.IsCacheHit)
            {
                var changedGeneration = generation;
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == changedGeneration)
                    {
                        ImageChanged?.Invoke(request.Path, request.DecodeHeight);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            request.Cancel();
        }
        catch (Exception exception)
        {
            request.Fail(exception);
        }
        finally
        {
            result?.Image?.Dispose();
            request.IsFinished = true;
            request.ReleaseIfUnused();
        }
    }

    private EmoticonBitmapLoadResult? Decode(string path, int decodeHeight, EmoticonBitmapCacheEntry? original,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            var fileLength = file.Length;
            var lastWrittenAt = file.LastWriteTimeUtc;
            if (original is not null && original.FileLength == fileLength && original.LastWrittenAt == lastWrittenAt)
            {
                return new(fileLength, lastWrittenAt, null);
            }
            using var stream = File.OpenRead(path);
            cancellationToken.ThrowIfCancellationRequested();
            LastDecodeThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref decodeCount);
            return new(fileLength, lastWrittenAt, Bitmap.DecodeToHeight(stream, decodeHeight));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException or NullReferenceException or OverflowException)
        {
            Trace.TraceWarning(Localization.Format("UI.EmoticonTextBlock.ImageDecodeFailed", exception.GetType().Name));
            return null;
        }
    }

    private async Task ValidateAsync(string path, int decodeHeight)
    {
        try
        {
            using var lease = await AcquireAsync(path, decodeHeight);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void Clear()
    {
        Dispatcher.UIThread.VerifyAccess();
        generation++;
        foreach (var request in pending.Values)
        {
            request.Cancel();
        }
        pending.Clear();
        foreach (var entry in recency)
        {
            entry.ReleaseRetention();
        }
        entries.Clear();
        recency.Clear();
        RetainedBytes = 0;
    }

    private void Remove(LinkedListNode<EmoticonBitmapCacheEntry> node)
    {
        var entry = node.Value;
        entries.Remove((entry.Path, entry.DecodeHeight));
        recency.Remove(node);
        RetainedBytes -= entry.ByteSize;
        entry.ReleaseRetention();
    }
}
