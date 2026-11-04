using System.Text;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Settings;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证设置分享格式、凭据保留和外部文件校验。</summary>
public sealed class SettingsShareServiceTests
{
    /// <summary>全部可迁移设置和 API Key 完整往返，本地身份不进入文件。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  test-key-测试\n  ")]
    public async Task AllPortableSettingsRoundTripWithoutChangingSource(string apiKey)
    {
        var settings = CreateCompleteSettings(apiKey);
        var original = JsonSerializer.Serialize(settings);
        var service = new SettingsShareService();
        await using var stream = new MemoryStream();

        await service.ExportAsync(settings, stream);

        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(new[] { "Format", "Version", "Settings" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(SettingsSharePackage.FORMAT, document.RootElement.GetProperty("Format").GetString());
        Assert.Equal(SettingsSharePackage.CURRENT_VERSION, document.RootElement.GetProperty("Version").GetInt32());
        var exportedSettings = document.RootElement.GetProperty("Settings");
        Assert.False(exportedSettings.TryGetProperty(nameof(PanelSettings.DefaultAccountId), out _));
        Assert.False(exportedSettings.TryGetProperty(nameof(PanelSettings.SelectedWorkspaceId), out _));
        Assert.Equal(apiKey, exportedSettings.GetProperty("Translation").GetProperty("ApiKey").GetString());
        stream.Position = 0;
        var imported = await service.ImportAsync(stream);

        AssertPortableSettingsEqual(settings, imported);
        Assert.Null(imported.DefaultAccountId);
        Assert.Null(imported.SelectedWorkspaceId);
        Assert.Equal(original, JsonSerializer.Serialize(settings));
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    /// <summary>应用配置可双向覆盖全部设置，同时保留现有全局设置对象和本地绑定。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyToReplacesEveryPortableSettingAndRetainsLocalIdentity(bool applyDefaults)
    {
        var source = applyDefaults ? new PanelSettings() : CreateCompleteSettings("  shared-key-测试  ");
        source.DefaultAccountId = "foreign-account";
        source.SelectedWorkspaceId = "foreign-workspace";
        var target = applyDefaults ? CreateCompleteSettings("old-key") : new PanelSettings();
        target.DefaultAccountId = "target-account";
        target.SelectedWorkspaceId = "target-workspace";
        var configuration = new PanelConfiguration { Settings = target };
        var originalSource = JsonSerializer.Serialize(source);

        new SettingsShareService().ApplyTo(configuration.Settings, source);

        Assert.Same(target, configuration.Settings);
        AssertPortableSettingsEqual(source, target);
        Assert.Equal("target-account", target.DefaultAccountId);
        Assert.Equal("target-workspace", target.SelectedWorkspaceId);
        Assert.Equal(originalSource, JsonSerializer.Serialize(source));
    }

    /// <summary>应用后的配色、术语表和阅读选项与来源完全独立。</summary>
    [Fact]
    public void ApplyToOwnsDetachedNestedSettings()
    {
        var source = CreateCompleteSettings("shared-key");
        var target = new PanelSettings();

        new SettingsShareService().ApplyTo(target, source);

        Assert.NotSame(source.ColorScheme, target.ColorScheme);
        Assert.NotSame(source.ColorScheme.Overrides, target.ColorScheme.Overrides);
        Assert.NotSame(source.ColorScheme.SavedSchemes, target.ColorScheme.SavedSchemes);
        Assert.NotSame(source.ColorScheme.SavedSchemes[0], target.ColorScheme.SavedSchemes[0]);
        Assert.NotSame(source.ColorScheme.SavedSchemes[0].Colors, target.ColorScheme.SavedSchemes[0].Colors);
        Assert.NotSame(source.Translation, target.Translation);
        Assert.NotSame(source.Translation.Glossary, target.Translation.Glossary);
        Assert.NotSame(source.Translation.Glossary[0], target.Translation.Glossary[0]);
        Assert.NotSame(source.Reading, target.Reading);
        Assert.NotSame(source.DanmuRegexFilter, target.DanmuRegexFilter);
        Assert.NotSame(source.DanmuRegexFilter.Patterns, target.DanmuRegexFilter.Patterns);
        var applied = JsonSerializer.Serialize(target);

        source.ColorScheme.Overrides.Clear();
        source.ColorScheme.SavedSchemes[0].Colors.Clear();
        source.ColorScheme.SavedSchemes[0].Name = "changed";
        source.Translation.ApiKey = "changed-key";
        source.Translation.Glossary[0].Target = "changed";
        source.Translation.Glossary.Clear();
        source.DanmuRegexFilter.Enabled = true;
        source.DanmuRegexFilter.Patterns.Clear();

        Assert.Equal(applied, JsonSerializer.Serialize(target));
    }

    /// <summary>任一配置分组验证失败都不能部分覆盖目标设置或替换其子对象。</summary>
    [Theory]
    [InlineData("appearance")]
    [InlineData("colors")]
    [InlineData("reading")]
    [InlineData("danmuRegexFilter")]
    [InlineData("translation")]
    public void ApplyToRejectsInvalidSourceBeforeChangingTarget(string invalidSection)
    {
        var source = CreateCompleteSettings("shared-key");
        switch (invalidSection)
        {
            case "appearance":
                source.MessageTextScale = double.NaN;
                break;
            case "colors":
                source.ColorScheme.Overrides["AppPrimaryBrush"] = "invalid";
                break;
            case "reading":
                source.Reading = new() { GiftAutoPinMinimum = -1 };
                break;
            case "danmuRegexFilter":
                source.DanmuRegexFilter.Patterns = ["["];
                break;
            case "translation":
                source.Translation.Glossary[0].Target = " ";
                break;
        }
        var target = new PanelSettings { DefaultAccountId = "local-account", SelectedWorkspaceId = "local-workspace" };
        var original = JsonSerializer.Serialize(target);
        var colors = target.ColorScheme;
        var reading = target.Reading;
        var translation = target.Translation;

        Assert.Throws<InvalidDataException>(() => new SettingsShareService().ApplyTo(target, source));

        Assert.Equal(original, JsonSerializer.Serialize(target));
        Assert.Same(colors, target.ColorScheme);
        Assert.Same(reading, target.Reading);
        Assert.Same(translation, target.Translation);
    }

    /// <summary>分享配置应用后，本地保存仍保留当前账号和工作区绑定及导入的 API Key。</summary>
    [Fact]
    public async Task AppliedSettingsKeepLocalBindingsInPersistentConfiguration()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new() { DefaultAccountId = "local-account", SelectedWorkspaceId = "local-workspace" }
        };
        var source = CreateCompleteSettings("  imported-key-测试  ");

        new SettingsShareService().ApplyTo(configuration.Settings, source);
        await store.SaveAsync(configuration);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal("local-account", document.RootElement.GetProperty(nameof(PanelSettings.DefaultAccountId)).GetString());
        Assert.Equal("local-workspace", document.RootElement.GetProperty(nameof(PanelSettings.SelectedWorkspaceId)).GetString());
        var restored = store.Load().Settings;
        Assert.Equal("local-account", restored.DefaultAccountId);
        Assert.Equal("local-workspace", restored.SelectedWorkspaceId);
        Assert.Equal(source.Translation.ApiKey, restored.Translation.ApiKey);
        AssertPortableSettingsEqual(source, restored);
    }

    /// <summary>分享包拥有独立的配色和术语表，编辑分享包不会修改当前设置。</summary>
    [Fact]
    public void PackageOwnsDetachedCopiesOfNestedSettings()
    {
        var settings = new PanelSettings
        {
            ColorScheme = new()
            {
                Overrides = new() { ["AppPrimaryBrush"] = "#123456" },
                SavedSchemes = [new() { Name = "个人", Colors = new() { ["AppPrimaryBrush"] = "#123456" } }]
            },
            Translation = new() { Glossary = [new() { Source = "主播", Target = "streamer" }] }
        };
        var original = JsonSerializer.Serialize(settings);

        var package = new SettingsShareService().CreatePackage(settings);
        package.Settings.ColorScheme.Overrides["AppPrimaryBrush"] = "#654321";
        package.Settings.ColorScheme.SavedSchemes[0].Colors.Clear();
        package.Settings.Translation.Glossary[0].Target = "changed";

        Assert.Equal(original, JsonSerializer.Serialize(settings));
    }

    /// <summary>较少字段的有效文件使用当前默认值，并忽略非身份类的扩展字段。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FutureOption\":true,\"DefaultAccountId\":\"foreign-account\",\"SelectedWorkspaceId\":\"foreign-workspace\"}")]
    public async Task OmittedSettingsKeepDefaultsAndLocalBindingsAreIgnored(string settingsJson)
    {
        await using var stream = OpenPackage(settingsJson);

        var imported = await new SettingsShareService().ImportAsync(stream);

        Assert.Equal(JsonSerializer.Serialize(new PanelSettings()), JsonSerializer.Serialize(imported));
    }

    /// <summary>错误类型、缺少身份字段、损坏内容及不支持的版本都不能变成默认设置导入。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"WorkspaceName\":\"Neo\",\"Rooms\":[]}")]
    [InlineData("{\"Version\":1,\"Settings\":{}}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Settings\":{}}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":1}")]
    [InlineData("{\"Format\":\"other\",\"Version\":1,\"Settings\":{}}")]
    [InlineData("{\"Format\":null,\"Version\":1,\"Settings\":{}}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":0,\"Settings\":{}}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":2,\"Settings\":{}}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":\"1\",\"Settings\":{}}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":1,\"Settings\":[]}")]
    [InlineData("{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":1,\"Settings\":null}")]
    public async Task InvalidPackagesAreRejected(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
        Assert.True(stream.CanRead);
    }

    /// <summary>显式空对象、非法枚举及超出控件范围的数值在导入边界拒绝。</summary>
    [Theory]
    [InlineData("\"Language\":null")]
    [InlineData("\"Language\":\" \"")]
    [InlineData("\"Theme\":null")]
    [InlineData("\"Theme\":\"unknown\"")]
    [InlineData("\"ColorScheme\":null")]
    [InlineData("\"ColorScheme\":{\"Overrides\":null}")]
    [InlineData("\"ColorScheme\":{\"SavedSchemes\":null}")]
    [InlineData("\"ColorScheme\":{\"SavedSchemes\":[null]}")]
    [InlineData("\"ColorScheme\":{\"Overrides\":{\"AppPrimaryBrush\":\"broken\"}}")]
    [InlineData("\"ColorScheme\":{\"Overrides\":{\"UnknownBrush\":\"#123456\"}}")]
    [InlineData("\"ColorScheme\":{\"SelectedSchemeId\":\"missing\"}")]
    [InlineData("\"Translation\":null")]
    [InlineData("\"Translation\":{\"ApiKey\":null}")]
    [InlineData("\"Translation\":{\"BaseUrl\":null}")]
    [InlineData("\"Translation\":{\"Model\":null}")]
    [InlineData("\"Translation\":{\"TargetLanguage\":null}")]
    [InlineData("\"Translation\":{\"ExtraSystemPrompt\":null}")]
    [InlineData("\"Translation\":{\"Glossary\":null}")]
    [InlineData("\"Translation\":{\"Glossary\":[null]}")]
    [InlineData("\"Translation\":{\"Glossary\":[{}]}")]
    [InlineData("\"Translation\":{\"DisplayMode\":-1}")]
    [InlineData("\"Translation\":{\"BacklogMode\":100}")]
    [InlineData("\"Reading\":null")]
    [InlineData("\"Reading\":{\"GiftReadResetPolicy\":3}")]
    [InlineData("\"Reading\":{\"GiftAutoPinMinimum\":-0.01}")]
    [InlineData("\"Reading\":{\"GiftAutoPinMinimum\":1000000001}")]
    [InlineData("\"PinyinLayout\":2")]
    [InlineData("\"MessageTextScale\":0.49")]
    [InlineData("\"MessageTextScale\":2.01")]
    [InlineData("\"MessageTextScale\":\"1\"")]
    [InlineData("\"ShowDanmuPinyin\":\"true\"")]
    public async Task InvalidSettingsAreRejected(string property)
    {
        await using var stream = OpenPackage("{" + property + "}");

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
    }

    /// <summary>启用翻译时沿用设置页的服务地址校验规则。</summary>
    [Theory]
    [InlineData("file:///tmp/api", "model", "ja-JP")]
    [InlineData("https://api.example.com/v1?key=value", "model", "ja-JP")]
    [InlineData("https://api.example.com/v1#fragment", "model", "ja-JP")]
    [InlineData("https://user:password@api.example.com/v1", "model", "ja-JP")]
    [InlineData("https://api.example.com/v1", " ", "ja-JP")]
    [InlineData("https://api.example.com/v1", "model", " ")]
    public async Task InvalidEnabledTranslationIsRejected(string baseUrl, string model, string language)
    {
        var settings = JsonSerializer.Serialize(new PanelSettings
        {
            Translation = new() { Enabled = true, BaseUrl = baseUrl, Model = model, TargetLanguage = language }
        });
        await using var stream = OpenPackage(settings);

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
    }

    /// <summary>控件边界数值和自定义语言代码保持可迁移。</summary>
    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(2, 1000000000)]
    public async Task SupportedNumericBoundariesAreAccepted(double scale, int giftMinimum)
    {
        var settings = JsonSerializer.Serialize(new PanelSettings
        {
            Language = "custom-language",
            MessageTextScale = scale,
            Reading = new() { GiftAutoPinMinimum = giftMinimum }
        });
        await using var stream = OpenPackage(settings);

        var imported = await new SettingsShareService().ImportAsync(stream);

        Assert.Equal(scale, imported.MessageTextScale);
        Assert.Equal(giftMinimum, imported.Reading.GiftAutoPinMinimum);
        Assert.Equal("custom-language", imported.Language);
    }

    /// <summary>无效的当前设置不能生成分享文件。</summary>
    [Fact]
    public async Task InvalidExportDoesNotWritePartialPackage()
    {
        var settings = new PanelSettings { MessageTextScale = double.NaN };
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ExportAsync(settings, stream));

        Assert.Equal(0, stream.Length);
    }

    /// <summary>取消信号向上传播，服务不接管调用方的流。</summary>
    [Fact]
    public async Task CancellationPropagatesAndLeavesStreamsOpen()
    {
        var service = new SettingsShareService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var source = OpenPackage("{}");
        await using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(source, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(new(), destination, cancellation.Token));

        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    private static PanelSettings CreateCompleteSettings(string apiKey)
    {
        return new()
        {
            Language = "ja-JP",
            Theme = "dark",
            ColorScheme = new()
            {
                Overrides = new() { ["AppPrimaryBrush"] = "#123456" },
                SelectedSchemeId = "night",
                SavedSchemes = [new() { Id = "night", Name = "夜间", Theme = "dark", Colors = new() { ["AppPrimaryBrush"] = "#123456" } }]
            },
            UseWindowMenuOnMac = true,
            CheckForUpdatesOnStartup = false,
            Translation = new()
            {
                Enabled = true,
                BaseUrl = "http://localhost:8080/v1",
                ApiKey = apiKey,
                Model = "translation-model",
                TargetLanguage = "es-MX",
                ExtraSystemPrompt = "保持人名",
                DisplayMode = TranslationDisplayMode.TranslationOnly,
                BacklogMode = TranslationBacklogMode.AllMessages,
                Glossary =
                [
                    new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" },
                    new() { Source = "主播", Target = "streamer", TargetLanguage = "es-MX" }
                ]
            },
            Reading = new() { GiftAutoPinMinimum = 12.5m, GiftReadResetPolicy = GiftReadResetPolicy.NewQuantity },
            ShowUserPinyin = true,
            ShowGiftPinyin = true,
            ShowDanmuPinyin = true,
            ShowSuperChatPinyin = true,
            PinyinLayout = PinyinLayoutMode.SeparateLine,
            MessageTextScale = 1.5,
            CompactDanmuDisplay = false,
            CompactDanmuWrap = true,
            CompactSuperChatWrap = false,
            AutoScrollLongMessages = false,
            HideEmoticonDanmu = true,
            DanmuRegexFilter = new() { Enabled = false, Patterns = ["^广告", "机器人$"] },
            DefaultAccountId = "local-account",
            SelectedWorkspaceId = "local-workspace"
        };
    }

    private static void AssertPortableSettingsEqual(PanelSettings expected, PanelSettings actual)
    {
        var expectedJson = JsonSerializer.SerializeToElement(expected);
        var actualJson = JsonSerializer.SerializeToElement(actual);
        foreach (var property in expectedJson.EnumerateObject().Where(property =>
                     property.Name is not (nameof(PanelSettings.DefaultAccountId) or nameof(PanelSettings.SelectedWorkspaceId))))
        {
            Assert.Equal(property.Value.GetRawText(), actualJson.GetProperty(property.Name).GetRawText());
        }
    }

    private static MemoryStream OpenPackage(string settingsJson)
    {
        var json = $$"""{"Format":"Yohuke.DanmuPanel.Settings","Version":1,"Settings":{{settingsJson}}}""";
        return new(Encoding.UTF8.GetBytes(json));
    }
}
