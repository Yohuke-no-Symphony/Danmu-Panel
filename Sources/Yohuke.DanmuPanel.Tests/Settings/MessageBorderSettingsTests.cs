using System.Text;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证三类消息边框的默认值、持久化、配置校验和分享隔离。</summary>
public sealed class MessageBorderSettingsTests
{
    /// <summary>新配置和缺失边框字段的旧配置均保持原有粗细。</summary>
    [Fact]
    public async Task NewAndLegacyConfigurationsKeepDefaultThickness()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        AssertThicknesses(2, 2, 2, store.Load().Settings.MessageBorders);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        var settings = store.Load().Settings;
        await using var package = OpenPackage("{}");
        var imported = await new SettingsShareService().ImportAsync(package);

        Assert.Equal("ja-JP", settings.Language);
        AssertThicknesses(2, 2, 2, settings.MessageBorders);
        AssertThicknesses(2, 2, 2, imported.MessageBorders);
        Assert.Equal(2, MessageBorderSettings.DEFAULT_THICKNESS);
        Assert.Equal(0.5, MessageBorderSettings.MIN_THICKNESS);
        Assert.Equal(10, MessageBorderSettings.MAX_THICKNESS);
    }

    /// <summary>局部配置只改变指定消息类型，其他类型沿用默认值。</summary>
    [Theory]
    [InlineData("{}", 2, 2, 2)]
    [InlineData("{\"SuperChat\":0.5}", 0.5, 2, 2)]
    [InlineData("{\"Gift\":2.75}", 2, 2.75, 2)]
    [InlineData("{\"Guard\":10}", 2, 2, 10)]
    public async Task PartialConfigurationsKeepUnspecifiedDefaults(string bordersJson, double superChat, double gift, double guard)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var settingsJson = "{\"MessageBorders\":" + bordersJson + "}";
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), settingsJson);
        await using var package = OpenPackage(settingsJson);

        var restored = store.Load().Settings;
        var imported = await new SettingsShareService().ImportAsync(package);

        AssertThicknesses(superChat, gift, guard, restored.MessageBorders);
        AssertThicknesses(superChat, gift, guard, imported.MessageBorders);
    }

    /// <summary>独立边框粗细保存到本地并通过分享文件完整恢复。</summary>
    [Fact]
    public async Task IndependentThicknessesRoundTripThroughConfigurationAndSharing()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var settings = new PanelSettings { MessageBorders = CreateBorders() };
        var sharing = new SettingsShareService();
        await using var package = new MemoryStream();

        await store.SaveAsync(new() { Settings = settings });
        await sharing.ExportAsync(settings, package);
        package.Position = 0;
        var restored = store.Load().Settings;
        var imported = await sharing.ImportAsync(package);

        AssertThicknesses(0.5, 2.75, 10, restored.MessageBorders);
        AssertThicknesses(0.5, 2.75, 10, imported.MessageBorders);
        Assert.NotSame(settings.MessageBorders, restored.MessageBorders);
        Assert.NotSame(settings.MessageBorders, imported.MessageBorders);
        Assert.Equal(0.5, restored.MessageBorders.GetThickness(MessageKind.SuperChat));
        Assert.Equal(2.75, restored.MessageBorders.GetThickness(MessageKind.Gift));
        Assert.Equal(10, restored.MessageBorders.GetThickness(MessageKind.Guard));
        Assert.Equal(0, restored.MessageBorders.GetThickness(MessageKind.Danmu));
        Assert.Throws<ArgumentOutOfRangeException>(() => restored.MessageBorders.GetThickness((MessageKind)(-1)));
    }

    /// <summary>上下界与非半像素的小数均属于有效配置，不被控件步进限制。</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.25)]
    [InlineData(10)]
    public async Task SupportedThicknessesAreAccepted(double thickness)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var settings = new PanelSettings
        {
            MessageBorders = new() { SuperChat = thickness, Gift = thickness, Guard = thickness }
        };
        await using var package = new MemoryStream();
        MessageBorderSettingsValidator.Validate(settings.MessageBorders);

        await store.SaveAsync(new() { Settings = settings });
        await new SettingsShareService().ExportAsync(settings, package);
        package.Position = 0;
        var imported = await new SettingsShareService().ImportAsync(package);

        AssertThicknesses(thickness, thickness, thickness, store.Load().Settings.MessageBorders);
        AssertThicknesses(thickness, thickness, thickness, imported.MessageBorders);
    }

    /// <summary>任一类型的非有限值或越界粗细均在写入前拒绝，不覆盖有效配置。</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.49)]
    [InlineData(10.01)]
    public async Task InvalidThicknessesCannotOverwriteConfigurationOrExport(double thickness)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = new() { MessageBorders = CreateBorders() } };
        await store.SaveAsync(configuration);
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var original = await File.ReadAllTextAsync(settingsPath);

        foreach (var kind in new[] { MessageKind.SuperChat, MessageKind.Gift, MessageKind.Guard })
        {
            configuration.Settings.MessageBorders = CreateBorders();
            SetThickness(configuration.Settings.MessageBorders, kind, thickness);
            await using var destination = new MemoryStream();

            Assert.Throws<InvalidDataException>(() => MessageBorderSettingsValidator.Validate(configuration.Settings.MessageBorders));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));
            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ExportAsync(configuration.Settings, destination));

            Assert.Equal(original, await File.ReadAllTextAsync(settingsPath));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
            Assert.Equal(0, destination.Length);
        }
    }

    /// <summary>显式空边框对象不能保存、导出或部分应用到运行设置。</summary>
    [Fact]
    public async Task NullBordersAreRejectedBeforeChangingAnySettings()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = new() { MessageBorders = CreateBorders() } };
        await store.SaveAsync(configuration);
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var originalFile = await File.ReadAllTextAsync(settingsPath);
        var target = configuration.Settings;
        var originalTarget = JsonSerializer.Serialize(target);
        var borders = target.MessageBorders;
        var source = new PanelSettings { Language = "ja-JP", MessageBorders = null! };
        var sharing = new SettingsShareService();
        await using var destination = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => MessageBorderSettingsValidator.Validate(source.MessageBorders));
        Assert.Throws<InvalidDataException>(() => sharing.ApplyTo(target, source));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new() { Settings = source }));
        await Assert.ThrowsAsync<InvalidDataException>(() => sharing.ExportAsync(source, destination));

        Assert.Equal(originalTarget, JsonSerializer.Serialize(target));
        Assert.Same(borders, target.MessageBorders);
        Assert.Equal(originalFile, await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(0, destination.Length);
    }

    /// <summary>损坏主文件保留原件并恢复包含三类自定义粗细的有效备份。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"SuperChat\":null}")]
    [InlineData("{\"Gift\":0.49}")]
    [InlineData("{\"Guard\":10.01}")]
    public async Task InvalidLocalBordersRestoreLastValidBackup(string bordersJson)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new() { Language = "ja-JP", MessageBorders = CreateBorders() }
        };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"MessageBorders\":" + bordersJson + "}");

        var restored = store.Load().Settings;

        Assert.Equal("ja-JP", restored.Language);
        AssertThicknesses(0.5, 2.75, 10, restored.MessageBorders);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
        AssertThicknesses(0.5, 2.75, 10, store.Load().Settings.MessageBorders);
    }

    /// <summary>分享包和应用后的设置拥有独立边框对象，并保留本地身份。</summary>
    [Fact]
    public void SharingCopiesBordersWithoutAliasingOrChangingLocalIdentity()
    {
        var source = new PanelSettings { Language = "ja-JP", MessageBorders = CreateBorders() };
        var target = new PanelSettings { DefaultAccountId = "local-account", SelectedWorkspaceId = "local-workspace" };
        var sharing = new SettingsShareService();

        var package = sharing.CreatePackage(source);
        sharing.ApplyTo(target, source);

        Assert.NotSame(source.MessageBorders, package.Settings.MessageBorders);
        Assert.NotSame(source.MessageBorders, target.MessageBorders);
        Assert.NotSame(package.Settings.MessageBorders, target.MessageBorders);
        AssertThicknesses(0.5, 2.75, 10, package.Settings.MessageBorders);
        AssertThicknesses(0.5, 2.75, 10, target.MessageBorders);
        Assert.Equal("ja-JP", target.Language);
        Assert.Equal("local-account", target.DefaultAccountId);
        Assert.Equal("local-workspace", target.SelectedWorkspaceId);
        source.MessageBorders.SuperChat = 3;
        package.Settings.MessageBorders.Gift = 4;
        target.MessageBorders.Guard = 5;

        AssertThicknesses(3, 2.75, 10, source.MessageBorders);
        AssertThicknesses(0.5, 4, 10, package.Settings.MessageBorders);
        AssertThicknesses(0.5, 2.75, 5, target.MessageBorders);
    }

    /// <summary>非法粗细在分享应用前整体拒绝，目标对象及其他字段保持原值。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public void InvalidSharedThicknessDoesNotPartiallyApplySettings(MessageKind kind)
    {
        var source = new PanelSettings { Language = "ja-JP", MessageBorders = CreateBorders() };
        SetThickness(source.MessageBorders, kind, double.NaN);
        var target = new PanelSettings { MessageBorders = CreateBorders() };
        var original = JsonSerializer.Serialize(target);
        var borders = target.MessageBorders;

        Assert.Throws<InvalidDataException>(() => new SettingsShareService().ApplyTo(target, source));

        Assert.Equal(original, JsonSerializer.Serialize(target));
        Assert.Same(borders, target.MessageBorders);
    }

    /// <summary>分享导入拒绝空引用、非法范围、非有限数值和错误字段类型。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"SuperChat\":null}")]
    [InlineData("{\"Gift\":null}")]
    [InlineData("{\"Guard\":null}")]
    [InlineData("{\"SuperChat\":0}")]
    [InlineData("{\"Gift\":0.49}")]
    [InlineData("{\"Guard\":10.01}")]
    [InlineData("{\"SuperChat\":1e1000}")]
    [InlineData("{\"Gift\":\"2\"}")]
    [InlineData("{\"Guard\":true}")]
    public async Task InvalidImportedBordersAreRejected(string bordersJson)
    {
        await using var package = OpenPackage("{\"MessageBorders\":" + bordersJson + "}");

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(package));

        Assert.True(package.CanRead);
    }

    private static MessageBorderSettings CreateBorders()
    {
        return new() { SuperChat = 0.5, Gift = 2.75, Guard = 10 };
    }

    private static void AssertThicknesses(double superChat, double gift, double guard, MessageBorderSettings actual)
    {
        Assert.Equal(superChat, actual.SuperChat);
        Assert.Equal(gift, actual.Gift);
        Assert.Equal(guard, actual.Guard);
    }

    private static void SetThickness(MessageBorderSettings settings, MessageKind kind, double thickness)
    {
        switch (kind)
        {
            case MessageKind.SuperChat:
                settings.SuperChat = thickness;
                break;
            case MessageKind.Gift:
                settings.Gift = thickness;
                break;
            case MessageKind.Guard:
                settings.Guard = thickness;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static MemoryStream OpenPackage(string settingsJson)
    {
        var json = $$"""{"Format":"Yohuke.DanmuPanel.Settings","Version":1,"Settings":{{settingsJson}}}""";
        return new(Encoding.UTF8.GetBytes(json));
    }
}
