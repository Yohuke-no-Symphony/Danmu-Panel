using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证粉丝牌显示偏好的兼容性、保存交互与分享。</summary>
public sealed class FanMedalSettingsTests
{
    /// <summary>旧配置和旧分享包没有粉丝牌偏好时保持显示。</summary>
    [Fact]
    public async Task ExistingConfigurationAndSharePackageKeepFanMedalsVisible()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), """{"Settings":{}}""");
        Assert.True(store.Load().Settings.ShowFanMedals);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Format":"Yohuke.DanmuPanel.Settings","Version":1,"Settings":{}}"""));

        var imported = await new SettingsShareService().ImportAsync(stream);

        Assert.True(imported.ShowFanMedals);
    }

    /// <summary>粉丝牌偏好经磁盘恢复及分享导入后仍可覆盖相反的目标设置。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceSurvivesReloadSharingAndApplying(bool shown)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new() { Settings = new() { ShowFanMedals = shown } });
        var restored = store.Load().Settings;
        Assert.Equal(shown, restored.ShowFanMedals);
        var sharing = new SettingsShareService();
        await using var stream = new MemoryStream();
        await sharing.ExportAsync(restored, stream);
        stream.Position = 0;
        var imported = await sharing.ImportAsync(stream);
        var target = new PanelSettings { ShowFanMedals = !shown };

        sharing.ApplyTo(target, imported);

        Assert.Equal(shown, target.ShowFanMedals);
    }

    /// <summary>粉丝牌开关独立于紧凑显示，只有保存后才改变运行配置和磁盘配置。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingSettingsOnlyApplyFanMedalPreferenceAfterSaving(bool initiallyShown)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { ShowFanMedals = initiallyShown, CompactDanmuDisplay = false }
        });
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionReading")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var check = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(),
                value => value.Name == "ShowFanMedalsCheckBox");

            Assert.Equal(initiallyShown, check.IsChecked);
            Assert.True(check.IsEnabled);
            Assert.True(check.IsEffectivelyVisible);

            check.IsChecked = !initiallyShown;

            Assert.Equal(initiallyShown, runtime.Configuration.Settings.ShowFanMedals);
            Assert.Equal(initiallyShown, runtime.ConfigurationStore.Load().Settings.ShowFanMedals);
            Assert.True(await view.SaveAsync());
            Assert.Equal(!initiallyShown, runtime.Configuration.Settings.ShowFanMedals);
            Assert.Equal(!initiallyShown, runtime.ConfigurationStore.Load().Settings.ShowFanMedals);
        }
        finally
        {
            window.Close();
        }
    }
}
