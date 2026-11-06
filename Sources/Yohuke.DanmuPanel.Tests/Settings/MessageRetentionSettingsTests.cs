using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证消息留存数量的默认值、持久化、损坏恢复与设置草稿。</summary>
public sealed class MessageRetentionSettingsTests
{
    /// <summary>新配置与缺失新增字段的旧配置均默认留存三百条消息。</summary>
    [Fact]
    public void NewAndLegacyConfigurationsDefaultToThreeHundred()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        Assert.Equal(300, store.Load().Settings.MessageRetentionCount);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        var settings = store.Load().Settings;

        Assert.Equal(300, settings.MessageRetentionCount);
        Assert.Equal("ja-JP", settings.Language);
    }

    /// <summary>自定义数量及上下界均能在重启后恢复。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(75)]
    [InlineData(300)]
    [InlineData(10000)]
    public async Task CountSurvivesConfigurationReload(int count)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new() { Settings = new() { MessageRetentionCount = count } });

        Assert.Equal(count, store.Load().Settings.MessageRetentionCount);
    }

    /// <summary>超出支持范围的本地配置恢复最近备份，并保留损坏原件。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10001)]
    public async Task InvalidLocalCountRestoresBackup(int count)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new() { MessageRetentionCount = 420, Language = "ja-JP" }
        };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), $$"""{"MessageRetentionCount":{{count}}}""");

        var restored = store.Load().Settings;

        Assert.Equal(420, restored.MessageRetentionCount);
        Assert.Equal("ja-JP", restored.Language);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
    }

    /// <summary>非法数量在保存前被拒绝，现有配置文件保持完整。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10001)]
    public async Task InvalidSaveKeepsExistingConfiguration(int count)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration();
        await store.SaveAsync(configuration);
        var settingsFile = Path.Combine(directory.Path, "settings.json");
        var original = await File.ReadAllTextAsync(settingsFile);
        configuration.Settings.MessageRetentionCount = count;

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));

        Assert.Equal(original, await File.ReadAllTextAsync(settingsFile));
    }

    /// <summary>阅读页显示默认数量，输入仅在保存后写入运行设置与配置文件。</summary>
    [AvaloniaFact]
    public async Task ReadingControlOnlyAppliesCountAfterSaving()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            SelectReading(view);
            var input = view.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(control => control.Name == "MessageRetentionCountInput");
            Assert.Equal(300m, input.Value);
            Assert.Equal(1m, input.Minimum);
            Assert.Equal(10000m, input.Maximum);

            input.Value = 475;

            Assert.Equal(300, runtime.Configuration.Settings.MessageRetentionCount);
            Assert.True(await view.SaveAsync());
            Assert.Equal(475, runtime.Configuration.Settings.MessageRetentionCount);
            Assert.Equal(475, runtime.ConfigurationStore.Load().Settings.MessageRetentionCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>空值及小数不能静默变成其他数量，也不能发布其他设置草稿。</summary>
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(12.5)]
    public async Task InvalidDraftDoesNotPublishSettings(double? value)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            SelectReading(view);
            var input = view.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(control => control.Name == "MessageRetentionCountInput");
            input.Value = value.HasValue ? (decimal)value.Value : null;

            Assert.False(await view.SaveAsync());
            Assert.Equal(300, runtime.Configuration.Settings.MessageRetentionCount);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            window.Close();
        }
    }

    private static void SelectReading(SettingsView view)
    {
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionReading")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
