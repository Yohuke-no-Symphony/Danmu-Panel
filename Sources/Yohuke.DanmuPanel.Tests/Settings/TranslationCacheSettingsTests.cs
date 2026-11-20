using Xunit;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证翻译缓存配置的版本兼容与损坏恢复。</summary>
public sealed class TranslationCacheSettingsTests
{
    /// <summary>旧配置缺少缓存字段时采用默认策略。</summary>
    [Fact]
    public async Task LegacyConfigurationUsesCacheDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), """{"Translation":{"Model":"legacy"}}""");

        var translation = store.Load().Settings.Translation;

        Assert.Equal("legacy", translation.Model);
        Assert.Equal(500, translation.CacheCapacity);
        Assert.Equal(360, translation.CacheTtlMinutes);
    }

    /// <summary>损坏的缓存策略恢复备份中的有效值。</summary>
    [Fact]
    public async Task InvalidCacheLifetimeRestoresConfigurationBackup()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = store.Load();
        configuration.Settings.Translation.CacheCapacity = 10000;
        configuration.Settings.Translation.CacheTtlMinutes = 43200;
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), """{"Translation":{"CacheTtlMinutes":0}}""");

        var translation = store.Load().Settings.Translation;

        Assert.Equal(10000, translation.CacheCapacity);
        Assert.Equal(43200, translation.CacheTtlMinutes);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
    }
}
