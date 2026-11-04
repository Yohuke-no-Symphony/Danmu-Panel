using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证正则屏蔽设置的兼容性、保存草稿和文件迁移。</summary>
public sealed class DanmuRegexSettingsTests
{
    /// <summary>无新字段的旧配置自动得到一次启用的同传规则。</summary>
    [Fact]
    public void LegacyConfigurationGetsDefaultRule()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        var settings = store.Load().Settings.DanmuRegexFilter;

        Assert.True(settings.Enabled);
        Assert.Equal(@"^(.*?)【(.*\*?)】$", Assert.Single(settings.Patterns));
    }

    /// <summary>开关、多个表达式及用户主动清空均能从磁盘恢复。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SettingsSurviveReloadWithoutReinsertingDefault(bool enabled, bool empty)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new() { DanmuRegexFilter = new() { Enabled = enabled, Patterns = empty ? [] : ["^广告", "机器人$"] } }
        };

        await store.SaveAsync(configuration);
        var reloaded = store.Load().Settings.DanmuRegexFilter;

        Assert.Equal(enabled, reloaded.Enabled);
        Assert.Equal(configuration.Settings.DanmuRegexFilter.Patterns, reloaded.Patterns);
    }

    /// <summary>禁用时仍保存规则，编辑和开关仅在保存后改变运行配置。</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DraftOnlyAppliesAfterSave(bool initiallyEnabled)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new() { DanmuRegexFilter = new() { Enabled = initiallyEnabled } }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionReading")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var check = view.GetVisualDescendants().OfType<CheckBox>()
                .Single(value => value.Name == "DanmuRegexFilterEnabledCheckBox");
            var input = view.GetVisualDescendants().OfType<TextBox>()
                .Single(value => value.Name == "DanmuRegexPatternsInput");
            Assert.Equal(initiallyEnabled, check.IsChecked);
            Assert.Equal(@"^(.*?)【(.*\*?)】$", input.Text);

            check.IsChecked = !initiallyEnabled;
            input.Text = "^广告\r\n\r\n机器人$\n   \n^ 保留空格 $\n";

            Assert.Equal(initiallyEnabled, configuration.Settings.DanmuRegexFilter.Enabled);
            Assert.Single(configuration.Settings.DanmuRegexFilter.Patterns);
            Assert.True(await view.SaveAsync());
            Assert.Equal(!initiallyEnabled, configuration.Settings.DanmuRegexFilter.Enabled);
            Assert.Equal(new[] { "^广告", "机器人$", "^ 保留空格 $" }, configuration.Settings.DanmuRegexFilter.Patterns);
            Assert.Equal(configuration.Settings.DanmuRegexFilter.Patterns, runtime.ConfigurationStore.Load().Settings.DanmuRegexFilter.Patterns);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>无效规则显示编辑框的实际行号，不保存、不关闭且保留原配置。</summary>
    [AvaloniaFact]
    public async Task InvalidDraftReportsLineAndDoesNotApply()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        var saved = false;
        view.Saved += () => saved = true;
        var original = JsonSerializer.Serialize(runtime.Configuration.Settings);
        try
        {
            window.Show();
            view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionReading")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var input = view.GetVisualDescendants().OfType<TextBox>()
                .Single(value => value.Name == "DanmuRegexPatternsInput");
            input.Text = "^广告\n\n[";

            Assert.False(await view.SaveAsync());

            Assert.False(saved);
            Assert.True(window.IsVisible);
            Assert.Equal("^广告\n\n[", input.Text);
            Assert.Equal(original, JsonSerializer.Serialize(runtime.Configuration.Settings));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == Localization.Format("Settings.DanmuRegexInvalidPattern", 3));
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>损坏的本地正则配置沿用备份恢复机制，原始文件被保留。</summary>
    [Fact]
    public async Task InvalidLocalRuleRestoresBackup()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new() { DanmuRegexFilter = new() { Enabled = false, Patterns = ["^广告"] } }
        };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"DanmuRegexFilter\":{\"Enabled\":true,\"Patterns\":[\"[\"]}}");

        var restored = store.Load().Settings.DanmuRegexFilter;

        Assert.False(restored.Enabled);
        Assert.Equal("^广告", Assert.Single(restored.Patterns));
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
    }

    /// <summary>规则及空列表可导入导出，应用后的集合与来源完全独立。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableRulesRoundTripAndApplyAsDetachedCopy(bool empty)
    {
        var source = new PanelSettings { DanmuRegexFilter = new() { Enabled = false, Patterns = empty ? [] : ["^广告", "机器人$"] } };
        var service = new SettingsShareService();
        await using var stream = new MemoryStream();
        await service.ExportAsync(source, stream);
        stream.Position = 0;
        var imported = await service.ImportAsync(stream);
        var target = new PanelSettings();

        service.ApplyTo(target, imported);

        Assert.False(target.DanmuRegexFilter.Enabled);
        Assert.Equal(source.DanmuRegexFilter.Patterns, target.DanmuRegexFilter.Patterns);
        Assert.NotSame(imported.DanmuRegexFilter, target.DanmuRegexFilter);
        Assert.NotSame(imported.DanmuRegexFilter.Patterns, target.DanmuRegexFilter.Patterns);
        imported.DanmuRegexFilter.Patterns.Add("changed");
        Assert.Equal(source.DanmuRegexFilter.Patterns, target.DanmuRegexFilter.Patterns);
    }

    /// <summary>外部文件中的无效正则和空配置结构均在应用前拒绝。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"Patterns\":null}")]
    [InlineData("{\"Patterns\":[null]}")]
    [InlineData("{\"Patterns\":[\"[\"]}")]
    [InlineData("{\"Patterns\":[\"^a\\nb$\"]}")]
    [InlineData("{\"Patterns\":[\"^a\\rb$\"]}")]
    public async Task InvalidImportedRulesAreRejected(string filterJson)
    {
        var json = "{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":1,\"Settings\":{\"DanmuRegexFilter\":" + filterJson + "}}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
    }
}
