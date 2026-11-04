using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证配置原子保存和损坏备份恢复。</summary>
public sealed class ConfigurationServiceTests
{
    /// <summary>旧设置保持默认配色；损坏颜色不会阻止从最近有效备份恢复。</summary>
    [Fact]
    public async Task InvalidColorSchemeRestoresBackupAndLegacySettingsKeepDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var service = new ConfigurationService(directory.Path);
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"Language\":\"ja-JP\"}");
        var configuration = service.Load();
        Assert.Empty(configuration.Settings.ColorScheme.Overrides);
        Assert.Empty(configuration.Settings.ColorScheme.SavedSchemes);
        await service.SaveAsync(configuration);
        File.WriteAllText(path, "{\"ColorScheme\":{\"Overrides\":{\"MessageOriginalBrush\":\"broken\"}}}");
        Assert.Equal("ja-JP", service.Load().Settings.Language);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
    }

    /// <summary>主文件损坏时保留原件并恢复最后一个完整备份。</summary>
    [Fact]
    public async Task CorruptPrimaryRestoresValidBackup()
    {
        using var directory = new MessageStoreTestDirectory();
        var service = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration();
        configuration.Settings.Language = "ja-JP";
        await service.SaveAsync(configuration);
        configuration.Settings.Language = "en-US";
        await service.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{");

        Assert.Equal("ja-JP", service.Load().Settings.Language);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
        Assert.Equal("ja-JP", service.Load().Settings.Language);
    }

    /// <summary>主文件与备份同时损坏时，两个原件均保留且应用可用默认值启动。</summary>
    [Fact]
    public void CorruptPrimaryAndBackupArePreservedBeforeUsingDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var service = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{");
        File.WriteAllText(Path.Combine(directory.Path, "settings.json.bak"), "null");

        Assert.Equal("system", service.Load().Settings.Language);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")).Length);
    }

    /// <summary>主文件缺失而备份存在时，恢复备份而不是重置用户配置。</summary>
    [Fact]
    public async Task MissingPrimaryRestoresBackup()
    {
        using var directory = new MessageStoreTestDirectory();
        var service = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration();
        configuration.Settings.Language = "zh-CN";
        await service.SaveAsync(configuration);
        await service.SaveAsync(configuration);
        File.Delete(Path.Combine(directory.Path, "settings.json"));

        Assert.Equal("zh-CN", service.Load().Settings.Language);
        Assert.True(File.Exists(Path.Combine(directory.Path, "settings.json")));
    }

    /// <summary>并发保存串行替换文件，每个快照保持有效且没有临时文件残留。</summary>
    [Fact]
    public async Task ConcurrentSavesCommitCompleteSnapshots()
    {
        using var directory = new MessageStoreTestDirectory();
        var service = new ConfigurationService(directory.Path);
        var writes = Enumerable.Range(0, 10).Select(index => service.SaveAsync(new()
        {
            Settings = new() { Language = "language-" + index },
            Workspaces = [new() { Id = "workspace-" + index, Name = "测试" }]
        })).ToArray();
        await Task.WhenAll(writes);

        var loaded = service.Load();
        Assert.Equal("language-9", loaded.Settings.Language);
        Assert.Equal("workspace-9", Assert.Single(loaded.Workspaces).Id);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
