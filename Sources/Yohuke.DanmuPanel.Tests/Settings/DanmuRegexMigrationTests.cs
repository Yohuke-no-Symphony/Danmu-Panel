using System.Text.Json;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证逐条正则格式、旧全局格式迁移和损坏配置的备份恢复。</summary>
public sealed class DanmuRegexMigrationTests
{
    /// <summary>旧全局开关复制到每条原始规则，再次保存只写新格式且不改变行为。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyFlagsMigrateToEachRuleAndWriteOnlyCanonicalFormat(bool enabled, bool blockTranslation)
    {
        var patterns = new[] { "^广告", "^ 保留空格 $" };
        var legacy = JsonSerializer.Serialize(new { Enabled = enabled, BlockTranslation = blockTranslation, Patterns = patterns });

        var migrated = JsonSerializer.Deserialize<DanmuRegexFilterSettings>(legacy)!;

        Assert.Equal(patterns, migrated.Rules.Select(rule => rule.Pattern));
        Assert.All(migrated.Rules, rule =>
        {
            Assert.Equal(enabled, rule.BlockDisplay);
            Assert.Equal(blockTranslation, rule.BlockTranslation);
        });
        var json = JsonSerializer.Serialize(migrated);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("Rules", Assert.Single(root.EnumerateObject()).Name);
        Assert.False(root.TryGetProperty("Enabled", out _));
        Assert.False(root.TryGetProperty("BlockTranslation", out _));
        Assert.False(root.TryGetProperty("Patterns", out _));
        var restored = JsonSerializer.Deserialize<DanmuRegexFilterSettings>(json)!;
        Assert.Equal(Values(migrated), Values(restored));
        Assert.NotSame(migrated.Rules, restored.Rules);
        Assert.NotSame(migrated.Rules[0], restored.Rules[0]);
    }

    /// <summary>旧缺失字段沿用默认规则与关闭状态，已有单开关作用于默认规则。</summary>
    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("{\"Enabled\":true}", true, false)]
    [InlineData("{\"BlockTranslation\":true}", false, true)]
    public void MissingLegacyFieldsUseCompatibleDefaults(string json, bool blockDisplay, bool blockTranslation)
    {
        var settings = JsonSerializer.Deserialize<DanmuRegexFilterSettings>(json)!;

        Assert.Equal(new[] { @"^(.*?)【(.*\*?)】$", @"(?:[♩♪♫♬♭♮♯]|🎵|🎶)" }, settings.Rules.Select(rule => rule.Pattern));
        Assert.All(settings.Rules, rule =>
        {
            Assert.Equal(blockDisplay, rule.BlockDisplay);
            Assert.Equal(blockTranslation, rule.BlockTranslation);
        });
    }

    /// <summary>缺少整个过滤设置时两条默认规则保持关闭，各实例及各行不共享可编辑对象。</summary>
    [Fact]
    public void MissingFilterCreatesDisabledIndependentDefaultRows()
    {
        var first = JsonSerializer.Deserialize<PanelSettings>("{}")!.DanmuRegexFilter;
        var second = JsonSerializer.Deserialize<PanelSettings>("{}")!.DanmuRegexFilter;

        Assert.Equal(2, first.Rules.Count);
        Assert.All(first.Rules, rule =>
        {
            Assert.False(rule.BlockDisplay);
            Assert.False(rule.BlockTranslation);
        });
        Assert.NotSame(first.Rules[0], first.Rules[1]);
        Assert.NotSame(first.Rules[0], second.Rules[0]);
        first.Rules[0].BlockDisplay = true;
        Assert.False(first.Rules[1].BlockDisplay);
        Assert.False(second.Rules[0].BlockDisplay);
    }

    /// <summary>两种格式中的显式空列表均持续保留为空。</summary>
    [Theory]
    [InlineData("{\"Patterns\":[]}")]
    [InlineData("{\"Enabled\":true,\"Patterns\":[]}")]
    [InlineData("{\"Rules\":[]}")]
    public void ExplicitEmptyRulesStayEmpty(string json)
    {
        var settings = JsonSerializer.Deserialize<DanmuRegexFilterSettings>(json)!;

        Assert.Empty(settings.Rules);
        Assert.Empty(JsonSerializer.Deserialize<DanmuRegexFilterSettings>(JsonSerializer.Serialize(settings))!.Rules);
    }

    /// <summary>新规则分别保留全部开关，深复制不会把草稿编辑传回原配置。</summary>
    [Fact]
    public void NewMixedRulesRoundTripWithDetachedRows()
    {
        var source = new DanmuRegexFilterSettings
        {
            Rules =
            [
                new() { Pattern = "完整", BlockDisplay = true },
                new() { Pattern = "翻译", BlockTranslation = true },
                new() { Pattern = "两个", BlockDisplay = true, BlockTranslation = true },
                new() { Pattern = "停用" }
            ]
        };
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, WriteIndented = true };

        var copy = JsonSerializer.Deserialize<DanmuRegexFilterSettings>(JsonSerializer.Serialize(source, options), options)!;

        Assert.Equal(Values(source), Values(copy));
        Assert.All(copy.Rules.Zip(source.Rules), pair => Assert.NotSame(pair.First, pair.Second));
        copy.Rules[0].Pattern = "已编辑";
        copy.Rules[0].BlockDisplay = false;
        copy.Rules.RemoveAt(1);
        Assert.Equal("完整", source.Rules[0].Pattern);
        Assert.True(source.Rules[0].BlockDisplay);
        Assert.Equal(4, source.Rules.Count);
    }

    /// <summary>新行省略可选布尔字段时默认关闭，并保留显式开启的另一个字段。</summary>
    [Theory]
    [InlineData("{\"Rules\":[{\"Pattern\":\"^广告\"}]}", false, false)]
    [InlineData("{\"Rules\":[{\"Pattern\":\"^广告\",\"BlockDisplay\":true}]}", true, false)]
    [InlineData("{\"Rules\":[{\"Pattern\":\"^广告\",\"BlockTranslation\":true}]}", false, true)]
    public void NewRowsUseFalseForMissingOptionalSwitches(string json, bool blockDisplay, bool blockTranslation)
    {
        var rule = Assert.Single(JsonSerializer.Deserialize<DanmuRegexFilterSettings>(json)!.Rules);

        Assert.Equal("^广告", rule.Pattern);
        Assert.Equal(blockDisplay, rule.BlockDisplay);
        Assert.Equal(blockTranslation, rule.BlockTranslation);
    }

    /// <summary>显式空值、错误类型、混合新旧格式和不完整行必须拒绝，不能静默重置。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("{\"Patterns\":null}")]
    [InlineData("{\"Patterns\":{}}")]
    [InlineData("{\"Patterns\":[null]}")]
    [InlineData("{\"Patterns\":[1]}")]
    [InlineData("{\"Enabled\":null}")]
    [InlineData("{\"Enabled\":\"true\"}")]
    [InlineData("{\"BlockTranslation\":1}")]
    [InlineData("{\"Rules\":null}")]
    [InlineData("{\"Rules\":{}}")]
    [InlineData("{\"Rules\":[null]}")]
    [InlineData("{\"Rules\":[\"^广告\"]}")]
    [InlineData("{\"Rules\":[{}]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":null}]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":1}]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":\"^广告\",\"BlockDisplay\":null}]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":\"^广告\",\"BlockTranslation\":\"true\"}]}")]
    [InlineData("{\"Rules\":[],\"Patterns\":[]}")]
    [InlineData("{\"Rules\":[],\"Enabled\":false}")]
    [InlineData("{\"Rules\":[],\"BlockTranslation\":false}")]
    [InlineData("{\"Rules\":[],\"Rules\":[]}")]
    [InlineData("{\"Enabled\":false,\"Enabled\":true}")]
    [InlineData("{\"Rules\":[{\"Pattern\":\"a\",\"Pattern\":\"b\"}]}")]
    public void MalformedAndAmbiguousStructuresAreRejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DanmuRegexFilterSettings>(json));
    }

    /// <summary>迁移只转换结构，无效正则仍由原有领域校验入口拒绝。</summary>
    [Theory]
    [InlineData("{\"Patterns\":[\"[\"]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":\"[\"}]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":\"\"}]}")]
    [InlineData("{\"Rules\":[{\"Pattern\":\"^a\\nb$\"}]}")]
    public void InvalidRegexStillUsesExistingValidation(string json)
    {
        var settings = JsonSerializer.Deserialize<DanmuRegexFilterSettings>(json)!;

        Assert.Throws<InvalidDataException>(() => DanmuRegexFilter.Validate(settings));
    }

    /// <summary>旧本地文件加载后保存为新格式，定制规则及每条行为保持不变。</summary>
    [Fact]
    public async Task LocalLegacyFileSavesAsNewRulesWithoutAppendingDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path,
            "{\"DanmuRegexFilter\":{\"Enabled\":false,\"BlockTranslation\":true,\"Patterns\":[\"^定制\"]}}");
        var configuration = store.Load();

        await store.SaveAsync(configuration);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var written = document.RootElement.GetProperty("DanmuRegexFilter");
        Assert.Equal("Rules", Assert.Single(written.EnumerateObject()).Name);
        var rule = Assert.Single(store.Load().Settings.DanmuRegexFilter.Rules);
        Assert.Equal("^定制", rule.Pattern);
        Assert.False(rule.BlockDisplay);
        Assert.True(rule.BlockTranslation);
    }

    /// <summary>新格式损坏仍保留原件并恢复既有有效备份。</summary>
    [Fact]
    public async Task InvalidNewStructureKeepsCorruptFileAndRestoresBackup()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                DanmuRegexFilter = new() { Rules = [new() { Pattern = "^备份", BlockDisplay = true }] }
            }
        };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        const string DAMAGED = "{\"DanmuRegexFilter\":{\"Rules\":null}}";
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), DAMAGED);

        var restored = Assert.Single(store.Load().Settings.DanmuRegexFilter.Rules);

        Assert.Equal("^备份", restored.Pattern);
        Assert.True(restored.BlockDisplay);
        var corrupt = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
        Assert.Equal(DAMAGED, await File.ReadAllTextAsync(corrupt));
    }

    private static (string Pattern, bool BlockDisplay, bool BlockTranslation)[] Values(DanmuRegexFilterSettings settings)
    {
        return settings.Rules.Select(rule => (rule.Pattern, rule.BlockDisplay, rule.BlockTranslation)).ToArray();
    }
}
