using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证全局特别关注名单的配置兼容、持久化和设置迁移边界。</summary>
public sealed class SpecialAttentionSettingsTests
{
    /// <summary>缺失结构、无效 UID、重复身份和空用户条目均属于损坏配置。</summary>
    public static TheoryData<string> InvalidSettings { get; } = new()
    {
        "null",
        "{\"Users\":null}",
        "{\"Users\":[null]}",
        "{\"Users\":[{}]}",
        "{\"Users\":[{\"UserUid\":0}]}",
        "{\"Users\":[{\"UserUid\":-1}]}",
        "{\"Users\":[{\"UserUid\":42,\"DisplayName\":null}]}",
        "{\"Users\":[{\"UserUid\":42,\"DisplayName\":\"first\"},{\"UserUid\":42,\"DisplayName\":\"renamed\"}]}"
    };

    /// <summary>首次启动和旧设置文件均默认使用空名单。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"Language\":\"ja-JP\"}")]
    [InlineData("{\"SpecialAttention\":{}}")]
    public void NewAndLegacyConfigurationsStartWithEmptyList(string? json)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(directory.Path, "settings.json"), json);
        }

        Assert.Empty(store.Load().Settings.SpecialAttention.Users);
    }

    /// <summary>名单按 UID 持久化，空名称、同名不同人和主动清空均能保留。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsersSurviveReloadIncludingEmptyList(bool empty)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = CreateSettings(empty) };

        await store.SaveAsync(configuration);
        var restored = store.Load().Settings.SpecialAttention;

        Assert.Equal(JsonSerializer.Serialize(configuration.Settings.SpecialAttention), JsonSerializer.Serialize(restored));
    }

    /// <summary>设置分享保留整份名单，应用后的集合和用户条目均与来源独立。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableUsersRoundTripAsDetachedCopy(bool empty)
    {
        var source = CreateSettings(empty);
        var sharing = new SettingsShareService();
        await using var stream = new MemoryStream();
        await sharing.ExportAsync(source, stream);
        stream.Position = 0;
        var imported = await sharing.ImportAsync(stream);
        var target = CreateSettings(!empty);

        sharing.ApplyTo(target, imported);

        Assert.Equal(JsonSerializer.Serialize(source.SpecialAttention), JsonSerializer.Serialize(target.SpecialAttention));
        Assert.NotSame(imported.SpecialAttention, target.SpecialAttention);
        Assert.NotSame(imported.SpecialAttention.Users, target.SpecialAttention.Users);
        for (var index = 0; index < imported.SpecialAttention.Users.Count; index++)
        {
            Assert.NotSame(imported.SpecialAttention.Users[index], target.SpecialAttention.Users[index]);
            imported.SpecialAttention.Users[index].DisplayName = "changed";
            imported.SpecialAttention.Users[index].UserUid = 999 + index;
        }
        imported.SpecialAttention.Users.Clear();
        Assert.Equal(JsonSerializer.Serialize(source.SpecialAttention), JsonSerializer.Serialize(target.SpecialAttention));
    }

    /// <summary>非法名单在写盘及应用分享配置前拒绝，不污染文件或运行配置。</summary>
    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task InvalidSettingsCannotOverwriteSavedFilesOrTarget(string attentionJson)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var target = CreateSettings();
        await store.SaveAsync(new() { Settings = target });
        var path = Path.Combine(directory.Path, "settings.json");
        var saved = await File.ReadAllTextAsync(path);
        var invalid = JsonSerializer.Deserialize<PanelSettings>("{\"SpecialAttention\":" + attentionJson + "}")!;
        var original = JsonSerializer.Serialize(target);
        var originalAttention = target.SpecialAttention;

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new() { Settings = invalid }));
        Assert.Throws<InvalidDataException>(() => new SettingsShareService().ApplyTo(target, invalid));

        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        Assert.Equal(original, JsonSerializer.Serialize(target));
        Assert.Same(originalAttention, target.SpecialAttention);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    /// <summary>无效的外部名单在导入阶段拒绝，避免稍后保存时才报错。</summary>
    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task InvalidImportedUsersAreRejected(string attentionJson)
    {
        var json = "{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":1,\"Settings\":{\"SpecialAttention\":" + attentionJson + "}}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
    }

    /// <summary>损坏的本地名单沿用备份恢复流程，并保留损坏文件。</summary>
    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task InvalidLocalUsersRestoreBackup(string attentionJson)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = CreateSettings() };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"SpecialAttention\":" + attentionJson + "}");

        var restored = store.Load().Settings.SpecialAttention;

        Assert.Equal(JsonSerializer.Serialize(configuration.Settings.SpecialAttention), JsonSerializer.Serialize(restored));
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
    }

    /// <summary>设置页尚未编辑名单时，保存其他选项也必须保留当前名单。</summary>
    [AvaloniaFact]
    public async Task SavingOtherSettingsPreservesSpecialAttention()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = CreateSettings() };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var previous = configuration.Settings.SpecialAttention;
        var expected = JsonSerializer.Serialize(previous);

        Assert.True(await view.SaveAsync());

        Assert.Equal(expected, JsonSerializer.Serialize(configuration.Settings.SpecialAttention));
        Assert.Equal(expected, JsonSerializer.Serialize(runtime.ConfigurationStore.Load().Settings.SpecialAttention));
        Assert.NotSame(previous, configuration.Settings.SpecialAttention);
        Assert.NotSame(previous.Users, configuration.Settings.SpecialAttention.Users);
    }

    private static PanelSettings CreateSettings(bool empty = false)
    {
        return new()
        {
            SpecialAttention = new()
            {
                Users = empty ? [] :
                [
                    new() { UserUid = 42, DisplayName = "同名用户" },
                    new() { UserUid = 73, DisplayName = "同名用户" },
                    new() { UserUid = long.MaxValue }
                ]
            }
        };
    }
}
