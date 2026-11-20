using System.Text.Json;

namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed class TranslationCacheSnapshotStore(string path, TranslationService service, Action<Exception> reportFailure)
{
    private const int SNAPSHOT_VERSION = 1;
    private readonly SemaphoreSlim diskGate = new(1, 1);
    private readonly string snapshotPath = Path.GetFullPath(path);
    private bool restoreCompleted;
    private bool canSave;

    internal Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        return RunSerializedAsync(() => RestoreCoreAsync(cancellationToken), cancellationToken);
    }

    internal Task SaveAsync()
    {
        return RunSerializedAsync(SaveCoreAsync);
    }

    internal Task ClearAsync()
    {
        return RunSerializedAsync(() =>
        {
            service.ClearCache();
            DeleteSnapshot();
            canSave = restoreCompleted;
            return Task.CompletedTask;
        });
    }

    private async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        if (restoreCompleted)
        {
            return;
        }

        var canSaveAfterRestore = true;
        try
        {
            await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            service.RestoreCache([], cancellationToken);
        }
        catch (JsonException exception)
        {
            reportFailure(exception);
            service.RestoreCache([], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reportFailure(exception);
            canSaveAfterRestore = false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        canSave = canSaveAfterRestore;
        restoreCompleted = true;
    }

    private async Task ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(TranslationCacheSnapshot.Version), out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var value))
        {
            throw new JsonException("The translation cache snapshot version is missing or invalid.");
        }
        if (value != SNAPSHOT_VERSION)
        {
            throw new NotSupportedException($"The translation cache snapshot version {value} is not supported.");
        }
        if (!root.TryGetProperty(nameof(TranslationCacheSnapshot.Entries), out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The translation cache snapshot entries are missing or invalid.");
        }

        service.RestoreCache(ReadEntries(entries, cancellationToken), cancellationToken);
    }

    private static IEnumerable<TranslationCacheEntry> ReadEntries(JsonElement entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = ReadText(entry, nameof(TranslationCacheEntry.Key));
            var translation = ReadText(entry, nameof(TranslationCacheEntry.Translation));
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(translation) &&
                entry.TryGetProperty(nameof(TranslationCacheEntry.StoredAtUtc), out var timestamp) &&
                timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var storedAtUtc) &&
                storedAtUtc != default)
            {
                yield return new(key, translation, storedAtUtc.ToUniversalTime());
            }
        }
    }

    private static string? ReadText(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return property.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task SaveCoreAsync()
    {
        if (!canSave)
        {
            return;
        }

        try
        {
            var entries = service.CaptureCacheSnapshot();
            if (entries.Length == 0)
            {
                DeleteSnapshot();
                return;
            }

            await WriteSnapshotAsync(new(SNAPSHOT_VERSION, entries)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            reportFailure(exception);
        }
    }

    private async Task WriteSnapshotAsync(TranslationCacheSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        var temporaryPath = $"{snapshotPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temporaryPath, snapshotPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteSnapshot()
    {
        try
        {
            File.Delete(snapshotPath);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task RunSerializedAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            diskGate.Release();
        }
    }
}
