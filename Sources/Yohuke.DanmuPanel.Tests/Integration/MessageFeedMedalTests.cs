using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证粉丝牌偏好原位更新真实消息并回收整列布局空间。</summary>
public sealed class MessageFeedMedalTests
{
    /// <summary>两种密度和窄面板中，置顶、普通及新消息共用粉丝牌开关，恢复时保留原始牌子。</summary>
    [AvaloniaTheory]
    [InlineData(false, 1.0, 760)]
    [InlineData(true, 1.0, 760)]
    [InlineData(false, 1.5, 360)]
    [InlineData(true, 1.5, 360)]
    public async Task PreferenceCollapsesAndRestoresMedalColumn(bool compact, double scale, int width)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { CompactDanmuDisplay = compact, MessageTextScale = scale }
        });
        var settings = runtime.Configuration.Settings;
        var withMedal = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu,
            UserName = "有粉丝牌", Content = "带牌子的弹幕", MedalName = "粉丝牌", MedalLevel = 20
        };
        var withoutMedal = withMedal with
        {
            Id = "without-medal", UserName = "无粉丝牌", MedalName = "", MedalLevel = 0
        };
        await runtime.Messages.AppendAsync(withMedal, settings.Reading);
        await runtime.Messages.AppendAsync(withoutMedal, settings.Reading);
        await runtime.Messages.TogglePinAsync(withMedal.Id);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Content = view, Width = width, Height = 480 };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var rows = view.GetVisualDescendants().OfType<MessageFeedRow>().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Single(view.Model.PinnedItems);
            var originalItems = view.Model.Items.ToArray();
            var originalWidths = rows.Select(row => Layout(row).ColumnDefinitions[0].ActualWidth).ToArray();
            Assert.All(rows, row => Assert.True(MedalSlot(row).IsEffectivelyVisible));
            Assert.All(originalWidths, columnWidth => Assert.True(columnWidth >= 66 * scale));
            Assert.Single(rows.SelectMany(row => row.GetVisualDescendants().OfType<Border>()),
                border => border.Classes.Contains("danmuMedalPlaceholder") && border.IsEffectivelyVisible);

            settings.ShowFanMedals = false;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Equal(originalItems, view.Model.Items);
            Assert.All(rows, row =>
            {
                Assert.Contains(row, view.GetVisualDescendants().OfType<MessageFeedRow>());
                Assert.False(MedalSlot(row).IsVisible);
                Assert.Equal(0, Layout(row).ColumnDefinitions[0].ActualWidth);
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<Border>(), border =>
                    (border.Classes.Contains("danmuMedalBadge") || border.Classes.Contains("danmuMedalPlaceholder")) &&
                    border.IsEffectivelyVisible);
            });
            var stored = (await runtime.Messages.GetByIdAsync(withMedal.Id))!;
            Assert.Equal(withMedal.MedalName, stored.MedalName);
            Assert.Equal(withMedal.MedalLevel, stored.MedalLevel);
            Assert.True(stored.IsPinned);

            await runtime.Messages.AppendAsync(withMedal with { Id = "new-medal-message" }, settings.Reading);
            await view.Model.RefreshAsync();
            Pump(window);
            var currentRows = view.GetVisualDescendants().OfType<MessageFeedRow>().ToArray();
            Assert.Equal(3, currentRows.Length);
            Assert.All(currentRows, row => Assert.False(MedalSlot(row).IsVisible));

            settings.ShowFanMedals = true;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.All(currentRows, row => Assert.True(MedalSlot(row).IsEffectivelyVisible));
            Assert.Equal(originalWidths, rows.Select(row => Layout(row).ColumnDefinitions[0].ActualWidth));
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static Grid MedalSlot(MessageFeedRow row)
    {
        return Assert.Single(row.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Classes.Contains("danmuMedalSlot"));
    }

    private static Grid Layout(MessageFeedRow row)
    {
        return Assert.Single(row.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Classes.Contains("danmuFeedLayout"));
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
