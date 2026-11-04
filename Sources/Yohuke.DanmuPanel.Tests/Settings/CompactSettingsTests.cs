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

/// <summary>验证紧凑偏好的默认值、持久化与设置草稿。</summary>
public sealed class CompactSettingsTests
{
    /// <summary>旧配置未包含紧凑字段时采用新的阅读默认值。</summary>
    [Fact]
    public void ExistingConfigurationWithoutCompactFieldsUsesTheReadingDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        var restored = store.Load().Settings;

        Assert.True(restored.CompactDanmuDisplay);
        Assert.False(restored.CompactDanmuWrap);
        Assert.True(restored.CompactSuperChatWrap);
        Assert.False(restored.AutoScrollLongMessages);
        Assert.Equal("ja-JP", restored.Language);
    }

    /// <summary>关闭紧凑显示后仍保存两组独立换行偏好。</summary>
    [Fact]
    public async Task DisabledCompactDisplayAndIndependentWrapPreferencesRoundTrip()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new()
        {
            Settings = new()
            {
                CompactDanmuDisplay = false,
                CompactDanmuWrap = true,
                CompactSuperChatWrap = false,
                AutoScrollLongMessages = true
            }
        });

        var restored = store.Load().Settings;

        Assert.False(restored.CompactDanmuDisplay);
        Assert.True(restored.CompactDanmuWrap);
        Assert.False(restored.CompactSuperChatWrap);
        Assert.True(restored.AutoScrollLongMessages);
    }

    /// <summary>主开关只禁用换行选项，草稿直到保存才应用到运行配置。</summary>
    [AvaloniaFact]
    public async Task CompactToggleRetainsWrapDraftsAndOnlyPublishesChangesOnSave()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        var settingsChanged = 0;
        runtime.Changed += _ => settingsChanged++;
        try
        {
            window.Show();
            view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionReading")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var compact = Check(view, "CompactDanmuDisplayCheckBox");
            var messages = Check(view, "CompactDanmuWrapCheckBox");
            var superChats = Check(view, "CompactSuperChatWrapCheckBox");
            var autoScroll = Check(view, "AutoScrollLongMessagesCheckBox");
            Assert.True(compact.IsChecked);
            Assert.False(messages.IsChecked);
            Assert.True(superChats.IsChecked);
            Assert.False(autoScroll.IsChecked);

            messages.IsChecked = true;
            autoScroll.IsChecked = true;
            superChats.IsChecked = false;
            compact.IsChecked = false;
            Assert.False(messages.IsEnabled);
            Assert.False(superChats.IsEnabled);
            Assert.False(autoScroll.IsEnabled);
            Assert.True(autoScroll.IsChecked);
            Assert.True(messages.IsChecked);
            Assert.False(superChats.IsChecked);
            compact.IsChecked = true;
            Assert.True(messages.IsEnabled);
            Assert.True(superChats.IsEnabled);
            Assert.True(autoScroll.IsEnabled);
            Assert.True(messages.IsChecked);
            Assert.False(superChats.IsChecked);
            compact.IsChecked = false;

            Assert.True(configuration.Settings.CompactDanmuDisplay);
            Assert.False(configuration.Settings.CompactDanmuWrap);
            Assert.False(configuration.Settings.AutoScrollLongMessages);
            Assert.True(configuration.Settings.CompactSuperChatWrap);
            Assert.Equal(0, settingsChanged);

            Assert.True(await view.SaveAsync());
            Assert.Equal(1, settingsChanged);
            var restored = runtime.ConfigurationStore.Load().Settings;
            Assert.False(restored.CompactDanmuDisplay);
            Assert.True(restored.CompactDanmuWrap);
            Assert.False(restored.CompactSuperChatWrap);
            Assert.True(restored.AutoScrollLongMessages);
        }
        finally
        {
            window.Close();
        }
    }

    private static CheckBox Check(Control view, string name)
    {
        return view.GetVisualDescendants().OfType<CheckBox>().Single(check => check.Name == name);
    }
}
