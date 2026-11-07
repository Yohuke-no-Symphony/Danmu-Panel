using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证逐条正则屏蔽配置的兼容性、设置草稿及文件迁移。</summary>
public sealed class DanmuRegexSettingsTests
{
    /// <summary>新配置和缺少规则的旧配置预填同传与音乐规则，每条规则的两个选项默认关闭。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{\"Language\":\"ja-JP\"}")]
    [InlineData("{\"DanmuRegexFilter\":{}}")]
    public void NewAndLegacyConfigurationsKeepDefaultRulesDisabled(string? json)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(directory.Path, "settings.json"), json);
        }

        var settings = store.Load().Settings.DanmuRegexFilter;

        Assert.Equal(new[] { @"^(.*?)【(.*\*?)】$", @"(?:[♩♪♫♬♭♮♯]|🎵|🎶)" }, settings.Rules.Select(rule => rule.Pattern));
        Assert.All(settings.Rules, rule =>
        {
            Assert.False(rule.BlockDisplay);
            Assert.False(rule.BlockTranslation);
        });
    }

    /// <summary>旧全局选项迁移到每条已有规则，保留规则顺序且不插入额外默认规则。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyConfigurationCopiesBlockingOptionsToExistingRules(bool enabled, bool blockTranslation)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), JsonSerializer.Serialize(new
        {
            DanmuRegexFilter = new { Enabled = enabled, BlockTranslation = blockTranslation, Patterns = new[] { "^广告", "歌词" } }
        }));

        var settings = store.Load().Settings.DanmuRegexFilter;

        Assert.Equal(new[] { "^广告", "歌词" }, settings.Rules.Select(rule => rule.Pattern));
        Assert.All(settings.Rules, rule =>
        {
            Assert.Equal(enabled, rule.BlockDisplay);
            Assert.Equal(blockTranslation, rule.BlockTranslation);
        });
    }

    /// <summary>缺少旧翻译开关的配置保留完整屏蔽行为，用户主动清空的列表保持为空。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyConfigurationPreservesMissingTranslationSwitchAndEmptyList(bool enabled, bool empty)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), JsonSerializer.Serialize(new
        {
            DanmuRegexFilter = new { Enabled = enabled, Patterns = empty ? Array.Empty<string>() : new[] { "^广告" } }
        }));

        var settings = store.Load().Settings.DanmuRegexFilter;

        Assert.Equal(empty ? 0 : 1, settings.Rules.Count);
        Assert.All(settings.Rules, rule =>
        {
            Assert.Equal("^广告", rule.Pattern);
            Assert.Equal(enabled, rule.BlockDisplay);
            Assert.False(rule.BlockTranslation);
        });
    }

    /// <summary>混合屏蔽选项与空规则列表均能从磁盘原样恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsSurviveReloadWithoutReinsertingDefault(bool empty)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = new() { DanmuRegexFilter = MixedRules(empty) } };

        await store.SaveAsync(configuration);

        AssertRulesEqual(configuration.Settings.DanmuRegexFilter, store.Load().Settings.DanmuRegexFilter);
    }

    /// <summary>两条规则分别编辑选项和表达式，保存前不生效，保存后匹配器及磁盘保持一致。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DraftOnlyAppliesAfterSave(bool blockDisplay, bool blockTranslation)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings.DanmuRegexFilter);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            SelectBlocking(window, view);
            EditRule(view.DanmuRegexEditor, 0, "^广告", blockDisplay, blockTranslation);
            EditRule(view.DanmuRegexEditor, 1, "^ 保留空格 $", !blockDisplay, !blockTranslation);

            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings.DanmuRegexFilter));
            Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings.DanmuRegexFilter));
            Assert.False(runtime.DanmuFilter.IsBlocked("广告内容"));
            Assert.False(runtime.DanmuFilter.IsTranslationBlocked("广告内容"));
            Assert.True(await view.SaveAsync());

            var applied = configuration.Settings.DanmuRegexFilter;
            Assert.Equal("^广告", applied.Rules[0].Pattern);
            Assert.Equal(blockDisplay, applied.Rules[0].BlockDisplay);
            Assert.Equal(blockTranslation, applied.Rules[0].BlockTranslation);
            Assert.Equal("^ 保留空格 $", applied.Rules[1].Pattern);
            Assert.Equal(!blockDisplay, applied.Rules[1].BlockDisplay);
            Assert.Equal(!blockTranslation, applied.Rules[1].BlockTranslation);
            Assert.Equal(blockDisplay, runtime.DanmuFilter.IsBlocked("广告内容"));
            Assert.Equal(blockDisplay || blockTranslation, runtime.DanmuFilter.IsTranslationBlocked("广告内容"));
            AssertRulesEqual(applied, store.Load().Settings.DanmuRegexFilter);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>版本二导出包含两条不同模式的规则草稿，不应用运行配置或磁盘设置。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExportPreservesIndependentRuleDraftsWithoutApplying(bool blockDisplay, bool blockTranslation)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, configuration);
        var original = JsonSerializer.Serialize(configuration.Settings.DanmuRegexFilter);
        var destination = new MemoryStream();
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = destination };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            SelectBlocking(window, view);
            EditRule(view.DanmuRegexEditor, 0, "^广告", blockDisplay, blockTranslation);
            EditRule(view.DanmuRegexEditor, 1, "歌词", !blockDisplay, !blockTranslation);

            Assert.True(await view.ExportSettingsAsync());

            using var document = JsonDocument.Parse(destination.ToArray());
            Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
            var filter = document.RootElement.GetProperty("Settings").GetProperty("DanmuRegexFilter");
            var rules = filter.GetProperty("Rules");
            Assert.Equal(2, rules.GetArrayLength());
            Assert.Equal("^广告", rules[0].GetProperty("Pattern").GetString());
            Assert.Equal(blockDisplay, rules[0].GetProperty("BlockDisplay").GetBoolean());
            Assert.Equal(blockTranslation, rules[0].GetProperty("BlockTranslation").GetBoolean());
            Assert.Equal("歌词", rules[1].GetProperty("Pattern").GetString());
            Assert.Equal(!blockDisplay, rules[1].GetProperty("BlockDisplay").GetBoolean());
            Assert.Equal(!blockTranslation, rules[1].GetProperty("BlockTranslation").GetBoolean());
            Assert.False(filter.TryGetProperty("Patterns", out _));
            Assert.False(filter.TryGetProperty("Enabled", out _));
            Assert.Equal(original, JsonSerializer.Serialize(configuration.Settings.DanmuRegexFilter));
            Assert.Equal(original, JsonSerializer.Serialize(store.Load().Settings.DanmuRegexFilter));
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>未完成或损坏的禁用规则按当前序号报错，保存与导出均不修改配置且保留草稿。</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[")]
    [InlineData("^a\nb$")]
    public async Task InvalidDraftReportsCurrentRuleAndDoesNotApply(string pattern)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = new MemoryStream() };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        var saved = false;
        view.Saved += () => saved = true;
        var original = JsonSerializer.Serialize(runtime.Configuration.Settings);
        try
        {
            SelectBlocking(window, view);
            var model = view.DanmuRegexEditor.Model;
            model.AddRuleCommand.Execute(null);
            model.Rules[2].Pattern = pattern;

            Assert.False(await view.SaveAsync());
            Assert.False(await view.ExportSettingsAsync());
            Assert.Equal(0, dialogs.ExportRequests);
            Assert.False(saved);
            Assert.True(window.IsVisible);
            Assert.Equal(pattern, model.Rules[2].Pattern);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == Localization.Format("Settings.DanmuRegexInvalidPattern", 3));
            model.RemoveRuleCommand.Execute(model.Rules[0]);
            Assert.False(await view.SaveAsync());
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == Localization.Format("Settings.DanmuRegexInvalidPattern", 2));
            Assert.Equal(original, JsonSerializer.Serialize(runtime.Configuration.Settings));
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));

            model.Rules[1].Pattern = "^修正";
            Assert.True(await view.SaveAsync());
            Assert.Equal("^修正", runtime.Configuration.Settings.DanmuRegexFilter.Rules[1].Pattern);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>损坏的旧正则配置沿用备份恢复机制，原始文件被保留。</summary>
    [Fact]
    public async Task InvalidLocalRuleRestoresBackup()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = new() { DanmuRegexFilter = MixedRules(false) } };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"DanmuRegexFilter\":{\"Enabled\":true,\"Patterns\":[\"[\"]}}");

        var restored = store.Load().Settings.DanmuRegexFilter;

        AssertRulesEqual(configuration.Settings.DanmuRegexFilter, restored);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
    }

    /// <summary>混合规则和空列表可导入导出，应用后的每条规则与来源完全独立。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableRulesRoundTripAndApplyAsDetachedCopy(bool empty)
    {
        var source = new PanelSettings { DanmuRegexFilter = MixedRules(empty) };
        var service = new SettingsShareService();
        await using var stream = new MemoryStream();
        await service.ExportAsync(source, stream);
        stream.Position = 0;
        var imported = await service.ImportAsync(stream);
        var target = new PanelSettings();

        service.ApplyTo(target, imported);

        AssertRulesEqual(source.DanmuRegexFilter, target.DanmuRegexFilter);
        Assert.NotSame(imported.DanmuRegexFilter, target.DanmuRegexFilter);
        Assert.NotSame(imported.DanmuRegexFilter.Rules, target.DanmuRegexFilter.Rules);
        if (!empty)
        {
            Assert.NotSame(imported.DanmuRegexFilter.Rules[0], target.DanmuRegexFilter.Rules[0]);
            imported.DanmuRegexFilter.Rules[0].Pattern = "changed";
            imported.DanmuRegexFilter.Rules[0].BlockDisplay = false;
        }
        imported.DanmuRegexFilter.Rules.Clear();
        AssertRulesEqual(source.DanmuRegexFilter, target.DanmuRegexFilter);
    }

    /// <summary>版本一与版本二文件中的无效规则或空结构均在应用前拒绝。</summary>
    [Theory]
    [InlineData(1, "null")]
    [InlineData(1, "{\"Patterns\":null}")]
    [InlineData(1, "{\"Patterns\":[null]}")]
    [InlineData(1, "{\"Patterns\":[\"[\"]}")]
    [InlineData(1, "{\"Patterns\":[\"^a\\nb$\"]}")]
    [InlineData(2, "null")]
    [InlineData(2, "{\"Rules\":null}")]
    [InlineData(2, "{\"Rules\":[null]}")]
    [InlineData(2, "{\"Rules\":[{\"Pattern\":null}]}")]
    [InlineData(2, "{\"Rules\":[{\"Pattern\":\"[\"}]}")]
    [InlineData(2, "{\"Rules\":[{\"Pattern\":\"^a\\nb$\"}]}")]
    public async Task InvalidImportedRulesAreRejected(int version, string filterJson)
    {
        var json = "{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":" + version +
                   ",\"Settings\":{\"DanmuRegexFilter\":" + filterJson + "}}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
    }

    private static DanmuRegexFilterSettings MixedRules(bool empty)
    {
        return new()
        {
            Rules = empty ? [] :
            [
                new() { Pattern = "^广告", BlockDisplay = true },
                new() { Pattern = "歌词", BlockTranslation = true }
            ]
        };
    }

    private static void AssertRulesEqual(DanmuRegexFilterSettings expected, DanmuRegexFilterSettings actual)
    {
        Assert.Equal(expected.Rules.Select(rule => (rule.Pattern, rule.BlockDisplay, rule.BlockTranslation)),
            actual.Rules.Select(rule => (rule.Pattern, rule.BlockDisplay, rule.BlockTranslation)));
    }

    private static void SelectBlocking(Window window, SettingsView view)
    {
        window.Show();
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionBlocking")
            .ExecuteBoundCommand();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void EditRule(DanmuRegexEditor editor, int index, string pattern, bool blockDisplay, bool blockTranslation)
    {
        var rule = editor.Model.Rules[index];
        var controls = editor.GetVisualDescendants().OfType<Control>().Where(control => ReferenceEquals(control.DataContext, rule)).ToArray();
        controls.OfType<TextBox>().Single(control => control.Name == "DanmuRegexPatternInput").Text = pattern;
        controls.OfType<CheckBox>().Single(control => control.Name == "DanmuRegexBlockDisplayCheckBox").IsChecked = blockDisplay;
        controls.OfType<CheckBox>().Single(control => control.Name == "DanmuRegexBlockTranslationCheckBox").IsChecked = blockTranslation;
    }
}
