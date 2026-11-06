using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.Models.Settings;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证外观页的三类边框输入、草稿隔离和设置迁移。</summary>
public sealed class MessageBorderAppearanceTests
{
    /// <summary>三类粗细独立编辑，保存后一起发布并持久化。</summary>
    [AvaloniaFact]
    public async Task IndependentThicknessDraftsArePublishedOnlyAfterSaving()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        var changes = 0;
        runtime.Changed += _ => changes++;
        try
        {
            window.Show();
            Pump(window);
            var superChat = Find<NumericUpDown>(view, "SuperChatBorderThicknessInput");
            var gift = Find<NumericUpDown>(view, "GiftBorderThicknessInput");
            var guard = Find<NumericUpDown>(view, "GuardBorderThicknessInput");
            foreach (var input in new[] { superChat, gift, guard })
            {
                Assert.Equal(2m, input.Value);
                Assert.Equal(0.5m, input.Minimum);
                Assert.Equal(10m, input.Maximum);
                Assert.Equal(0.5m, input.Increment);
                Assert.True(input.IsEffectivelyVisible);
            }
            superChat.Value = 0.5m;
            gift.Value = 3.5m;
            guard.Value = 10m;
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.SuperChat);
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.Gift);
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.Guard);
            Assert.Equal(0, changes);

            Assert.True(await view.SaveAsync());
            Assert.Equal(1, changes);
            var restored = runtime.ConfigurationStore.Load().Settings.MessageBorders;
            Assert.Equal(0.5, restored.SuperChat);
            Assert.Equal(3.5, restored.Gift);
            Assert.Equal(10, restored.Guard);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>清空任意一类粗细都阻止保存，运行配置和磁盘配置保持有效值。</summary>
    [AvaloniaTheory]
    [InlineData("SuperChatBorderThicknessInput")]
    [InlineData("GiftBorderThicknessInput")]
    [InlineData("GuardBorderThicknessInput")]
    public async Task MissingThicknessBlocksSavingWithoutChangingConfiguration(string inputName)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new());
        var original = await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json"));
        await using var runtime = new PanelRuntime(store, store.Load());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            Pump(window);
            Find<NumericUpDown>(view, inputName).Value = null;
            Assert.False(await view.SaveAsync());
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.SuperChat);
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.Gift);
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.Guard);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>导出包含尚未发布的三类边框草稿，导入后能独立保存。</summary>
    [AvaloniaFact]
    public async Task ThicknessDraftsExportAndImportThroughSettingsWindow()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        using var exported = new MemoryStream();
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = exported };
        var view = new SettingsView(runtime, new TextBlock(), false, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            Pump(window);
            Find<NumericUpDown>(view, "SuperChatBorderThicknessInput").Value = 1.25m;
            Find<NumericUpDown>(view, "GiftBorderThicknessInput").Value = 4m;
            Find<NumericUpDown>(view, "GuardBorderThicknessInput").Value = 8.5m;
            Assert.True(await view.ExportSettingsAsync());
            var bytes = exported.ToArray();
            var package = JsonSerializer.Deserialize<SettingsSharePackage>(bytes)!;
            Assert.Equal(1.25, package.Settings.MessageBorders.SuperChat);
            Assert.Equal(4, package.Settings.MessageBorders.Gift);
            Assert.Equal(8.5, package.Settings.MessageBorders.Guard);
            Assert.Equal(2, runtime.Configuration.Settings.MessageBorders.SuperChat);
            dialogs.ImportSource = new MemoryStream(bytes);
            Assert.True(await view.ImportSettingsAsync());
            var restored = runtime.ConfigurationStore.Load().Settings.MessageBorders;
            Assert.Equal(1.25, restored.SuperChat);
            Assert.Equal(4, restored.Gift);
            Assert.Equal(8.5, restored.Guard);
        }
        finally
        {
            window.Close();
        }
    }

    private static T Find<T>(Control control, string name) where T : Control
    {
        return control.GetLogicalDescendants().OfType<T>().Single(value => value.Name == name);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
