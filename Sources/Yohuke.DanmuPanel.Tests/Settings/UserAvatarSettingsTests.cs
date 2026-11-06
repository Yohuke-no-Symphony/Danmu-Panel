using System.Text.Json;
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

/// <summary>验证用户头像显示偏好的默认值、持久化、设置草稿与分享。</summary>
public sealed class UserAvatarSettingsTests
{
    /// <summary>旧配置缺少头像偏好时默认显示用户头像。</summary>
    [Fact]
    public void ExistingConfigurationEnablesUserAvatars()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        Assert.True(store.Load().Settings.ShowUserAvatars);
    }

    /// <summary>显示与隐藏头像的偏好均能从磁盘恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceSurvivesConfigurationReload(bool shown)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new() { Settings = new() { ShowUserAvatars = shown } });

        Assert.Equal(shown, store.Load().Settings.ShowUserAvatars);
    }

    /// <summary>阅读页的头像开关独立于紧凑显示，保存前不改变运行配置或磁盘配置。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingOnlyAppliesAfterSaving(bool initiallyShown)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration
        {
            Settings = new() { ShowUserAvatars = initiallyShown, CompactDanmuDisplay = false }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        await runtime.ConfigurationStore.SaveAsync(configuration);
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
                value => value.Name == "ShowUserAvatarsCheckBox");
            Assert.Equal(initiallyShown, check.IsChecked);
            Assert.True(check.IsEnabled);

            check.IsChecked = !initiallyShown;

            Assert.Equal(initiallyShown, configuration.Settings.ShowUserAvatars);
            Assert.Equal(initiallyShown, runtime.ConfigurationStore.Load().Settings.ShowUserAvatars);
            Assert.True(await view.SaveAsync());
            Assert.Equal(!initiallyShown, configuration.Settings.ShowUserAvatars);
            Assert.Equal(!initiallyShown, runtime.ConfigurationStore.Load().Settings.ShowUserAvatars);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>分享文件保留关闭头像的显式偏好，应用后覆盖目标的默认开启状态。</summary>
    [Fact]
    public async Task DisabledPreferenceSurvivesSharingAndApplying()
    {
        var service = new SettingsShareService();
        await using var stream = new MemoryStream();

        await service.ExportAsync(new() { ShowUserAvatars = false }, stream);

        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.False(document.RootElement.GetProperty("Settings").GetProperty("ShowUserAvatars").GetBoolean());
        stream.Position = 0;
        var imported = await service.ImportAsync(stream);
        Assert.False(imported.ShowUserAvatars);
        var target = new PanelSettings();
        service.ApplyTo(target, imported);
        Assert.False(target.ShowUserAvatars);
    }

    /// <summary>旧分享文件缺少头像偏好时使用默认开启状态。</summary>
    [Fact]
    public async Task ExistingSharePackageEnablesUserAvatars()
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"Format":"Yohuke.DanmuPanel.Settings","Version":1,"Settings":{}}"""));

        var imported = await new SettingsShareService().ImportAsync(stream);

        Assert.True(imported.ShowUserAvatars);
    }
}
