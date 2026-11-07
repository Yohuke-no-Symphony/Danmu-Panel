namespace Yohuke.DanmuPanel.Services;

/// <summary>提供 Panel 独立的缓存目录。</summary>
public sealed class AppDirectoryService
{
    public string ConfigDirectory { get; }
    public string EmoticonCacheDirectory { get; }
    public string AvatarCacheDirectory { get; }
    public string TranslationCacheDatabasePath { get; }

    /// <summary>初始化应用数据和图片缓存位置。</summary>
    public AppDirectoryService(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        EmoticonCacheDirectory = Path.Combine(configDirectory, "Cache", "Emoticons");
        AvatarCacheDirectory = Path.Combine(configDirectory, "Cache", "Avatars");
        TranslationCacheDatabasePath = Path.Combine(configDirectory, "translation-cache.db");
    }
}
