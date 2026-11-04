using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证消息文字缩放、拼音排版及正文开关的草稿隔离和持久化。</summary>
public sealed class ContentPinyinSettingsTests
{
    /// <summary>未包含正文拼音字段的旧配置保留原有外观，并默认使用逐字对齐排版。</summary>
    [Fact]
    public void LegacyConfigurationDefaultsToDisabledContentPinyinAndRubyLayout()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\",\"ShowUserPinyin\":true}");

        var restored = store.Load().Settings;

        Assert.False(restored.ShowDanmuPinyin);
        Assert.False(restored.ShowSuperChatPinyin);
        Assert.Equal(PinyinLayoutMode.Ruby, restored.PinyinLayout);
        Assert.Equal(1, restored.MessageTextScale);
        Assert.True(restored.ShowUserPinyin);
        Assert.Equal("ja-JP", restored.Language);
    }

    /// <summary>弹幕与 SC 开关的四种组合均独立保存，两种排版均可往返恢复。</summary>
    [Theory]
    [InlineData(false, false, PinyinLayoutMode.Ruby)]
    [InlineData(false, true, PinyinLayoutMode.Ruby)]
    [InlineData(true, false, PinyinLayoutMode.Ruby)]
    [InlineData(true, true, PinyinLayoutMode.Ruby)]
    [InlineData(false, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(false, true, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, true, PinyinLayoutMode.SeparateLine)]
    public async Task IndependentContentPinyinPreferencesRoundTrip(bool showDanmu, bool showSuperChat, PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new()
        {
            Settings = new()
            {
                ShowDanmuPinyin = showDanmu,
                ShowSuperChatPinyin = showSuperChat,
                PinyinLayout = layout
            }
        });

        var restored = store.Load().Settings;

        Assert.Equal(showDanmu, restored.ShowDanmuPinyin);
        Assert.Equal(showSuperChat, restored.ShowSuperChatPinyin);
        Assert.Equal(layout, restored.PinyinLayout);
    }

    /// <summary>整体文字缩放的边界值及常用比例均可保存并恢复为百分比输入。</summary>
    [AvaloniaTheory]
    [InlineData(0.5d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public async Task MessageTextScaleRoundTripsAndInitializesThePercentageInput(double textScale)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = new() { MessageTextScale = textScale } };
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(configuration);
        await using var runtime = new PanelRuntime(store, store.Load());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            Find<Button>(view, "SettingsSectionReading").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var input = Find<NumericUpDown>(view, "MessageTextScaleInput");

            Assert.Equal((decimal)textScale * 100, input.Value);
            Assert.Equal(50, input.Minimum);
            Assert.Equal(200, input.Maximum);
            Assert.Equal(10, input.Increment);
            Assert.True(await view.SaveAsync());
            Assert.Equal(textScale, store.Load().Settings.MessageTextScale);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>阅读页的拼音开关、排版和整体文字缩放仅在保存时一起发布并持久化。</summary>
    [AvaloniaFact]
    public async Task ContentPinyinDraftIsPublishedOnlyAfterSaving()
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
            Find<Button>(view, "SettingsSectionReading").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var danmu = Find<CheckBox>(view, "ShowDanmuPinyinCheckBox");
            var superChat = Find<CheckBox>(view, "ShowSuperChatPinyinCheckBox");
            var layout = Find<ComboBox>(view, "PinyinLayoutComboBox");
            var textScale = Find<NumericUpDown>(view, "MessageTextScaleInput");
            Assert.False(danmu.IsChecked);
            Assert.False(superChat.IsChecked);
            Assert.Equal(0, layout.SelectedIndex);
            Assert.Equal(100, textScale.Value);

            danmu.IsChecked = true;
            superChat.IsChecked = true;
            layout.SelectedIndex = 1;
            textScale.Value = 125;

            Assert.False(configuration.Settings.ShowDanmuPinyin);
            Assert.False(configuration.Settings.ShowSuperChatPinyin);
            Assert.Equal(PinyinLayoutMode.Ruby, configuration.Settings.PinyinLayout);
            Assert.Equal(1, configuration.Settings.MessageTextScale);
            Assert.Equal(0, settingsChanged);
            Assert.True(await view.SaveAsync());
            Assert.Equal(1, settingsChanged);
            var restored = runtime.ConfigurationStore.Load().Settings;
            Assert.True(restored.ShowDanmuPinyin);
            Assert.True(restored.ShowSuperChatPinyin);
            Assert.Equal(PinyinLayoutMode.SeparateLine, restored.PinyinLayout);
            Assert.Equal(1.25, restored.MessageTextScale);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>其他设置校验失败时，保留拼音草稿且不改变运行配置或关闭窗口。</summary>
    [AvaloniaFact]
    public async Task InvalidTranslationRetainsContentPinyinDraftWithoutPublishing()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new() { Translation = new() { Enabled = true, Model = "" } }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        var saved = false;
        view.Saved += () => saved = true;
        var settingsChanged = 0;
        runtime.Changed += _ => settingsChanged++;
        try
        {
            window.Show();
            Find<Button>(view, "SettingsSectionReading").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var danmu = Find<CheckBox>(view, "ShowDanmuPinyinCheckBox");
            var superChat = Find<CheckBox>(view, "ShowSuperChatPinyinCheckBox");
            var layout = Find<ComboBox>(view, "PinyinLayoutComboBox");
            var textScale = Find<NumericUpDown>(view, "MessageTextScaleInput");
            danmu.IsChecked = true;
            superChat.IsChecked = true;
            layout.SelectedIndex = 1;
            textScale.Value = 150;

            Assert.False(await view.SaveAsync());

            Assert.True(window.IsVisible);
            Assert.False(saved);
            Assert.Equal(0, settingsChanged);
            Assert.False(configuration.Settings.ShowDanmuPinyin);
            Assert.False(configuration.Settings.ShowSuperChatPinyin);
            Assert.Equal(PinyinLayoutMode.Ruby, configuration.Settings.PinyinLayout);
            Assert.Equal(1, configuration.Settings.MessageTextScale);
            Assert.True(danmu.IsChecked);
            Assert.True(superChat.IsChecked);
            Assert.Equal(1, layout.SelectedIndex);
            Assert.Equal(150, textScale.Value);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            window.Close();
        }
    }

    private static T Find<T>(Control view, string name) where T : Control
    {
        return Assert.Single(view.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    }
}
