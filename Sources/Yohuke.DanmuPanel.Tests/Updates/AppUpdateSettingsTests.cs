using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Updates;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Updates;

/// <summary>验证更新偏好兼容旧配置、单独保存和手动入口的草稿隔离。</summary>
public sealed class AppUpdateSettingsTests
{
    /// <summary>旧配置缺少字段时默认启用，显式禁用时可保存并恢复。</summary>
    [Fact]
    public async Task StartupPreferenceDefaultsOnAndPersistsWhenDisabled()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), """{"Language":"ja-JP"}""");
        var configuration = store.Load();
        Assert.True(configuration.Settings.CheckForUpdatesOnStartup);
        configuration.Settings.CheckForUpdatesOnStartup = false;
        await store.SaveAsync(configuration);
        Assert.False(store.Load().Settings.CheckForUpdatesOnStartup);
        Assert.Equal("ja-JP", store.Load().Settings.Language);
    }

    /// <summary>切换开关即时保存；手动检查保留其他页未保存的输入且不触发整页保存。</summary>
    [AvaloniaFact]
    public async Task UpdatePagePreservesOtherDraftsAndSavesOnlyItsPreference()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = new() { Translation = new() { Model = "before" } } };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        var checks = 0;
        var saved = false;
        var changed = false;
        view.CheckForUpdatesRequested += () => checks++;
        view.StartupUpdatePreferenceChanged += value => changed = !value;
        view.Saved += () => saved = true;
        try
        {
            window.Show();
            Click(view, "SettingsSectionTranslation");
            var model = view.GetVisualDescendants().OfType<TextBox>().Single(text => text.Text == "before");
            model.Text = "unsaved draft";
            Click(view, "SettingsSectionUpdates");
            window.UpdateLayout();
            Assert.Equal(0, checks);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Name == "CurrentAppVersion" && text.Text == AppUpdateVersion.Current.ToString());
            var checkbox = view.GetVisualDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "CheckForUpdatesOnStartupCheckBox");
            Assert.True(checkbox.IsChecked);
            checkbox.IsChecked = false;
            await view.PendingUpdatePreferenceSave;
            Assert.True(changed);
            Assert.False(runtime.ConfigurationStore.Load().Settings.CheckForUpdatesOnStartup);
            Assert.Equal("before", runtime.ConfigurationStore.Load().Settings.Translation.Model);
            Click(view, "CheckForUpdatesButton");
            Assert.Equal(1, checks);
            Assert.False(saved);
            Assert.True(window.IsVisible);
            if (Environment.GetEnvironmentVariable("DANMUPANEL_UPDATE_SCREENSHOTS") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(output, "update-settings.png"));
            }
            Click(view, "SettingsSectionTranslation");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), text => ReferenceEquals(model, text));
            Assert.Equal("unsaved draft", model.Text);
        }
        finally
        {
            await view.PendingUpdatePreferenceSave;
            window.Close();
        }
    }

    private static void Click(Control view, string name)
    {
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
