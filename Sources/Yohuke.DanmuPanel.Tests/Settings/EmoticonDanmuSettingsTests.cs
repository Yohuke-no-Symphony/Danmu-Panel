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

/// <summary>验证纯表情隐藏偏好的兼容性、持久化及设置草稿。</summary>
public sealed class EmoticonDanmuSettingsTests
{
    /// <summary>旧配置没有隐藏偏好时继续显示全部弹幕。</summary>
    [Fact]
    public void ExistingConfigurationKeepsEmoticonDanmuVisible()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        Assert.False(store.Load().Settings.HideEmoticonDanmu);
    }

    /// <summary>隐藏与显示两种偏好均能从磁盘恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceSurvivesConfigurationReload(bool hidden)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new() { Settings = new() { HideEmoticonDanmu = hidden } });

        Assert.Equal(hidden, store.Load().Settings.HideEmoticonDanmu);
    }

    /// <summary>隐藏开关独立于紧凑显示，保存前保持运行配置不变。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingOnlyAppliesAfterSaving(bool initiallyHidden)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new() { HideEmoticonDanmu = initiallyHidden, CompactDanmuDisplay = false }
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
                .Single(value => value.Name == "HideEmoticonDanmuCheckBox");
            Assert.Equal(initiallyHidden, check.IsChecked);
            Assert.True(check.IsEnabled);

            check.IsChecked = !initiallyHidden;

            Assert.Equal(initiallyHidden, configuration.Settings.HideEmoticonDanmu);
            Assert.True(await view.SaveAsync());
            Assert.Equal(!initiallyHidden, configuration.Settings.HideEmoticonDanmu);
            Assert.Equal(!initiallyHidden, runtime.ConfigurationStore.Load().Settings.HideEmoticonDanmu);
        }
        finally
        {
            window.Close();
        }
    }
}
