using Xunit;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证本地缓存统计的边界、递归目录和翻译缓存文件。</summary>
public sealed class CacheSizeServiceTests
{
    /// <summary>尚未生成缓存时返回零，统计不会创建应用目录。</summary>
    [Fact]
    public async Task MissingCachesReturnZeroWithoutCreatingDirectories()
    {
        using var directory = new MessageStoreTestDirectory();
        var service = new CacheSizeService(new(directory.Path));

        Assert.Equal(0, await service.GetSizeAsync());
        Assert.False(Directory.Exists(directory.Path));
    }

    /// <summary>头像、表情、翻译快照和旧缓存文件计入总量，历史、配置及浏览器资料排除。</summary>
    [Fact]
    public async Task CountsImageCachesAndBothTranslationCacheFormats()
    {
        using var directory = new MessageStoreTestDirectory();
        var paths = new AppDirectoryService(directory.Path);
        WriteFile(Path.Combine(paths.AvatarCacheDirectory, "avatar.img"), 11);
        WriteFile(Path.Combine(paths.AvatarCacheDirectory, "Users", ".metadata.json"), 13);
        WriteFile(Path.Combine(paths.EmoticonCacheDirectory, "nested", "emoticon.img"), 17);
        WriteFile(paths.TranslationCacheDatabasePath, 19);
        WriteFile(paths.TranslationCacheDatabasePath + "-wal", 23);
        WriteFile(paths.TranslationCacheDatabasePath + "-shm", 29);
        WriteFile(paths.TranslationCacheDatabasePath + "-journal", 31);
        WriteFile(paths.TranslationCacheSnapshotPath, 37);
        WriteFile(Path.Combine(directory.Path, "messages.db"), 1000);
        WriteFile(Path.Combine(directory.Path, "messages.db-wal"), 1000);
        WriteFile(Path.Combine(directory.Path, "settings.json"), 1000);
        WriteFile(Path.Combine(directory.Path, "translation-cache.db.bak"), 1000);
        WriteFile(Path.Combine(directory.Path, "Browser", "Cache", "data"), 1000);
        var service = new CacheSizeService(paths);

        Assert.Equal(180, await service.GetSizeAsync());

        File.Delete(Path.Combine(paths.AvatarCacheDirectory, "avatar.img"));
        Assert.Equal(169, await service.GetSizeAsync());
    }

    /// <summary>符号链接不引入目录外文件，也不会在循环链接中无限递归。</summary>
    [Fact]
    public async Task SkipsSymbolicLinksToFilesDirectoriesAndCacheRoots()
    {
        using var directory = new MessageStoreTestDirectory();
        var paths = new AppDirectoryService(directory.Path);
        var outside = Path.Combine(directory.Path, "OtherData");
        var outsideFile = Path.Combine(outside, "history.db");
        WriteFile(outsideFile, 1000);
        WriteFile(Path.Combine(paths.AvatarCacheDirectory, "avatar.img"), 7);
        Directory.CreateSymbolicLink(Path.Combine(paths.AvatarCacheDirectory, "outside"), outside);
        Directory.CreateSymbolicLink(Path.Combine(paths.AvatarCacheDirectory, "loop"), paths.AvatarCacheDirectory);
        File.CreateSymbolicLink(Path.Combine(paths.AvatarCacheDirectory, "linked.img"), outsideFile);
        Directory.CreateSymbolicLink(paths.EmoticonCacheDirectory, outside);
        File.CreateSymbolicLink(paths.TranslationCacheDatabasePath, outsideFile);
        File.CreateSymbolicLink(paths.TranslationCacheSnapshotPath, outsideFile);

        Assert.Equal(7, await new CacheSizeService(paths).GetSizeAsync());
    }

    /// <summary>取消请求在开始扫描前生效。</summary>
    [Fact]
    public async Task HonorsCancellationBeforeScanning()
    {
        using var directory = new MessageStoreTestDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CacheSizeService(new(directory.Path)).GetSizeAsync(cancellation.Token));
    }

    private static void WriteFile(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }
}
