namespace Yohuke.DanmuPanel.Services.Settings;

/// <summary>只读统计头像、表情及翻译缓存的本地文件总大小。</summary>
public sealed class CacheSizeService(AppDirectoryService directories)
{
    private static string[] TranslationDatabaseSuffixes { get; } = ["", "-wal", "-shm", "-journal"];

    /// <summary>在后台统计缓存文件，跳过符号链接及扫描期间已经删除的文件。</summary>
    public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var total = GetDirectorySize(directories.AvatarCacheDirectory, cancellationToken);
            total = checked(total + GetDirectorySize(directories.EmoticonCacheDirectory, cancellationToken));
            foreach (var suffix in TranslationDatabaseSuffixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + GetFileSize(directories.TranslationCacheDatabasePath + suffix));
            }
            return total;
        }, cancellationToken);
    }

    private static long GetDirectorySize(string directoryPath, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(directoryPath);
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        var total = 0L;
        while (pending.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry is DirectoryInfo)
                    {
                        pending.Push(entry.FullName);
                    }
                    else
                    {
                        total = checked(total + GetFileSize(entry.FullName));
                    }
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // 后台清理可能移除正在枚举的目录，其余缓存目录仍需继续统计。
            }
        }
        return total;
    }

    private static long GetFileSize(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return (file.Attributes & FileAttributes.ReparsePoint) == 0 ? file.Length : 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return 0;
        }
    }
}
