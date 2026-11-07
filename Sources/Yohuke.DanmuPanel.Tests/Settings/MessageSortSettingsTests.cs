using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Settings;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证六项排序配置的默认值、持久化、分享校验和设置草稿。</summary>
public sealed class MessageSortSettingsTests
{
    /// <summary>旧本地配置和旧分享包缺少排序字段时使用新的默认顺序。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MessageRetentionCount\":475}")]
    public async Task OldSettingsUseTheNewDefaultOrders(string settingsJson)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), settingsJson);
        var settings = new ConfigurationService(directory.Path).Load().Settings;
        await using var stream = Package(settingsJson);
        var imported = await new SettingsShareService().ImportAsync(stream);
        Assert.Equal(settings.MessageRetentionCount, imported.MessageRetentionCount);

        foreach (var kind in new[] { MessageKind.SuperChat, MessageKind.Gift, MessageKind.Guard })
        {
            Assert.Equal(MessageSortOrder.OldestFirst, settings.MessageSorting.GetPanel(kind).Unread);
            Assert.Equal(MessageSortOrder.NewestFirst, settings.MessageSorting.GetPanel(kind).Read);
            Assert.Equal(settings.MessageSorting.GetPanel(kind), imported.MessageSorting.GetPanel(kind));
        }
    }

    /// <summary>六项配置分别保存，配置重载及分享导入不会丢失，也不会共用原设置实例。</summary>
    [Fact]
    public async Task SixOrdersSurviveConfigurationAndSettingsSharing()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = new() { MessageSorting = CustomOrders() } };
        var service = new ConfigurationService(directory.Path);
        await service.SaveAsync(configuration);
        Assert.Equal(configuration.Settings.MessageSorting, service.Load().Settings.MessageSorting);

        var sharing = new SettingsShareService();
        await using var stream = new MemoryStream();
        await sharing.ExportAsync(configuration.Settings, stream);
        stream.Position = 0;
        var imported = await sharing.ImportAsync(stream);
        var target = new PanelSettings();
        sharing.ApplyTo(target, imported);
        Assert.Equal(configuration.Settings.MessageSorting, target.MessageSorting);
        Assert.NotSame(imported.MessageSorting, target.MessageSorting);
        foreach (var kind in new[] { MessageKind.SuperChat, MessageKind.Gift, MessageKind.Guard })
        {
            Assert.NotSame(imported.MessageSorting.GetPanel(kind), target.MessageSorting.GetPanel(kind));
        }
    }

    /// <summary>非法方向和缺失分组在分享导入时拒绝，本地损坏设置则恢复备份。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"SuperChat\":null}")]
    [InlineData("{\"Gift\":null}")]
    [InlineData("{\"Guard\":null}")]
    [InlineData("{\"SuperChat\":{\"Unread\":-1}}")]
    [InlineData("{\"SuperChat\":{\"Read\":2}}")]
    [InlineData("{\"Gift\":{\"Unread\":2}}")]
    [InlineData("{\"Gift\":{\"Read\":-1}}")]
    [InlineData("{\"Guard\":{\"Unread\":-1}}")]
    [InlineData("{\"Guard\":{\"Read\":2}}")]
    public async Task InvalidOrdersAreRejectedAndLocalBackupIsRestored(string value)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = new() { MessageSorting = CustomOrders() } };
        var service = new ConfigurationService(directory.Path);
        await service.SaveAsync(configuration);
        await service.SaveAsync(configuration);
        var invalidJson = "{\"MessageSorting\":" + value + "}";
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), invalidJson);
        Assert.Equal(configuration.Settings.MessageSorting, service.Load().Settings.MessageSorting);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));

        var invalid = JsonSerializer.Deserialize<PanelSettings>(invalidJson)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveAsync(new() { Settings = invalid }));
        await using var stream = Package(invalidJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ImportAsync(stream));
    }

    /// <summary>设置页提供六个独立下拉框，修改草稿后仅在保存时应用并写入配置。</summary>
    [AvaloniaFact]
    public async Task ReadingPageSavesSixIndependentDraftOrders()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 1050 };
        try
        {
            window.Show();
            SelectReading(view);
            if (Environment.GetEnvironmentVariable("DANMUPANEL_MESSAGE_SORT_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                var language = Localization.SelectedLanguageID;
                try
                {
                    Localization.SetLanguage("zh-CN");
                    Pump(window);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(screenshot);
                }
                finally
                {
                    Localization.SetLanguage(language);
                }
            }
            var expected = CustomOrders();
            foreach (var kind in new[] { MessageKind.SuperChat, MessageKind.Gift, MessageKind.Guard })
            {
                var unread = Choice(view, kind + "UnreadSortOrderComboBox");
                var read = Choice(view, kind + "ReadSortOrderComboBox");
                Assert.Equal(0, unread.SelectedIndex);
                Assert.Equal(1, read.SelectedIndex);
                unread.SelectedIndex = (int)expected.GetPanel(kind).Unread;
                read.SelectedIndex = (int)expected.GetPanel(kind).Read;
            }
            Assert.Equal(new MessageSortSettings(), runtime.Configuration.Settings.MessageSorting);
            Assert.True(await view.SaveAsync());
            Assert.Equal(expected, runtime.Configuration.Settings.MessageSorting);
            Assert.Equal(expected, runtime.ConfigurationStore.Load().Settings.MessageSorting);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>排序草稿未选择方向时拒绝保存，不发布其他配置。</summary>
    [AvaloniaFact]
    public async Task InvalidDraftDoesNotPublishSettings()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            SelectReading(view);
            Choice(view, "GiftReadSortOrderComboBox").SelectedIndex = -1;
            Assert.False(await view.SaveAsync());
            Assert.Equal(new MessageSortSettings(), runtime.Configuration.Settings.MessageSorting);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            window.Close();
        }
    }

    private static MessageSortSettings CustomOrders()
    {
        return new()
        {
            SuperChat = new() { Unread = MessageSortOrder.NewestFirst, Read = MessageSortOrder.OldestFirst },
            Gift = new() { Unread = MessageSortOrder.NewestFirst, Read = MessageSortOrder.NewestFirst },
            Guard = new() { Unread = MessageSortOrder.OldestFirst, Read = MessageSortOrder.OldestFirst }
        };
    }

    private static MemoryStream Package(string settings, int version = SettingsSharePackage.CURRENT_VERSION)
    {
        return new(Encoding.UTF8.GetBytes("{\"Format\":\"" + SettingsSharePackage.FORMAT + "\",\"Version\":" + version + ",\"Settings\":" + settings + "}"));
    }

    private static ComboBox Choice(Control view, string name)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(), choice => choice.Name == name);
    }

    private static void SelectReading(SettingsView view)
    {
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSectionReading")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
