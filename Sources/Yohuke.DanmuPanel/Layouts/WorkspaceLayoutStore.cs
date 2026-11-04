using System.Text.Json;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed class WorkspaceLayoutStore
{
    private const string FILE_NAME = "layouts.json";
    private const int DEFAULT_BACKUP_RETENTION_COUNT = 10;
    private readonly string configDirectory;
    private readonly Func<int> getBackupRetentionCount;
    private readonly Action<string, string> replaceFile;
    private readonly SemaphoreSlim fileGate = new(1, 1);
    private long lastBackupTimestamp;

    internal WorkspaceLayoutStore(
        string configDirectory,
        Func<int>? getBackupRetentionCount = null,
        Action<string, string>? replaceFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        this.configDirectory = configDirectory;
        this.getBackupRetentionCount = getBackupRetentionCount ?? (() => DEFAULT_BACKUP_RETENTION_COUNT);
        this.replaceFile = replaceFile ?? ((source, destination) => File.Move(source, destination, true));
        FilePath = Path.Combine(configDirectory, FILE_NAME);
        BackupDirectory = Path.Combine(configDirectory, "Backups");
        CorruptDirectory = Path.Combine(configDirectory, "Corrupt");
    }

    internal string FilePath { get; }

    internal string BackupDirectory { get; }

    internal string CorruptDirectory { get; }

    internal async Task<WorkspaceLayoutFile> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var (file, migrated) = await TryReadAsync(FilePath, cancellationToken).ConfigureAwait(false);

            if (file is not null)
            {
                if (migrated)
                {
                    await WriteAtomicAsync(WorkspaceLayoutSerializer.Serialize(file), true, cancellationToken).ConfigureAwait(false);
                }
                return file;
            }

            var primaryExists = File.Exists(FilePath);
            var backups = EnumerateBackups().ToArray();

            if (primaryExists)
            {
                PreserveCorruptFile();
            }

            foreach (var backup in backups)
            {
                (file, _) = await TryReadAsync(backup, cancellationToken).ConfigureAwait(false);

                if (file is null)
                {
                    continue;
                }

                await WriteAtomicAsync(WorkspaceLayoutSerializer.Serialize(file), false, cancellationToken)
                    .ConfigureAwait(false);
                return file;
            }

            file = primaryExists || backups.Length > 0
                ? WorkspaceLayoutPresets.CreateDefaultFile()
                : WorkspaceLayoutPresets.CreateDefaultFile();
            await WriteAtomicAsync(WorkspaceLayoutSerializer.Serialize(file), false, cancellationToken)
                .ConfigureAwait(false);
            return file;
        }
        finally
        {
            fileGate.Release();
        }
    }

    internal async Task SaveAsync(WorkspaceLayoutFile file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = WorkspaceLayoutSerializer.Serialize(file);
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteAtomicAsync(json, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
    }

    private static async Task<(WorkspaceLayoutFile? File, bool Migrated)> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (null, false);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            var file = WorkspaceLayoutSerializer.Deserialize(json, out var migrated);
            return (file, migrated);
        }
        catch (JsonException)
        {
            return (null, false);
        }
        catch (InvalidDataException)
        {
            return (null, false);
        }
    }

    private async Task WriteAtomicAsync(string json, bool createBackup, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(configDirectory);
        var temporaryPath = Path.Combine(configDirectory, $"{FILE_NAME}.{Guid.NewGuid():N}.tmp");
        var retention = Math.Clamp(getBackupRetentionCount(), 0, 100);
        string? createdBackupPath = null;
        var committed = false;

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (createBackup && retention > 0 && File.Exists(FilePath))
            {
                Directory.CreateDirectory(BackupDirectory);
                createdBackupPath = CreateUniqueHistoryPath(BackupDirectory, "bak");
                File.Copy(FilePath, createdBackupPath);
                File.SetLastWriteTimeUtc(createdBackupPath, new DateTime(lastBackupTimestamp, DateTimeKind.Utc));
            }

            cancellationToken.ThrowIfCancellationRequested();
            replaceFile(temporaryPath, FilePath);
            committed = true;

            if (createBackup && retention > 0)
            {
                foreach (var backupPath in EnumerateBackups().Skip(retention))
                {
                    File.Delete(backupPath);
                }
            }
        }
        finally
        {
            try
            {
                if (!committed && createdBackupPath is not null)
                {
                    File.Delete(createdBackupPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private void PreserveCorruptFile()
    {
        Directory.CreateDirectory(CorruptDirectory);
        File.Copy(FilePath, CreateUniqueHistoryPath(CorruptDirectory, "corrupt"));
    }

    private string CreateUniqueHistoryPath(string directory, string extension)
    {
        lastBackupTimestamp = Math.Max(DateTime.UtcNow.Ticks, lastBackupTimestamp + 1);
        var timestamp = new DateTime(lastBackupTimestamp, DateTimeKind.Utc);
        return Path.Combine(directory, $"{FILE_NAME}.{timestamp:yyyyMMdd-HHmmss-fffffff}.{Guid.NewGuid():N}.{extension}");
    }

    private IEnumerable<string> EnumerateBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(BackupDirectory, $"{FILE_NAME}.*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.Ordinal);
    }
}
