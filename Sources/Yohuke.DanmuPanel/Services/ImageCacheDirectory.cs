namespace Yohuke.DanmuPanel.Services;

internal sealed class ImageCacheDirectory(string directoryPath)
{
    private readonly SemaphoreSlim mutationLock = new(1, 1);
    private long revision;

    internal string DirectoryPath { get; } = directoryPath;
    internal long Revision => Interlocked.Read(ref revision);

    internal async Task ClearAsync()
    {
        await mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var attributes = File.GetAttributes(DirectoryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Cache path is not a directory: {DirectoryPath}");
                }
                Directory.Delete(DirectoryPath, recursive: true);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        finally
        {
            Interlocked.Increment(ref revision);
            mutationLock.Release();
        }
    }

    internal static async Task<ReadOnlyMemory<byte>> ReadHeaderAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 1, useAsync: true);
            var header = new byte[12];
            var length = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            return header.AsMemory(0, length);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    internal Task<bool> TryWriteAsync(string path, ReadOnlyMemory<byte> bytes, long expectedRevision,
        CancellationToken cancellationToken, Action? afterWrite = null)
    {
        return TryMutateAsync(expectedRevision, async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
                afterWrite?.Invoke();
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }, cancellationToken);
    }

    internal Task<bool> TryDeleteAsync(string path, long expectedRevision)
    {
        return TryMutateAsync(expectedRevision, () =>
        {
            File.Delete(path);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private async Task<bool> TryMutateAsync(long expectedRevision, Func<Task> mutate, CancellationToken cancellationToken)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Revision != expectedRevision)
            {
                return false;
            }
            await mutate().ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }
}
