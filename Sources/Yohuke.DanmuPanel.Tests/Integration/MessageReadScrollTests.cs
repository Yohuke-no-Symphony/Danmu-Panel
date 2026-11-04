using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证点击已读并重新排序时保留当前阅读位置。</summary>
public sealed class MessageReadScrollTests
{
    /// <summary>阅读历史或停留底部时，点击视口顶部或中部消息均保留邻近未读消息的屏幕坐标。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, 0, false)]
    [InlineData(MessageKind.SuperChat, 2, false)]
    [InlineData(MessageKind.Gift, 0, false)]
    [InlineData(MessageKind.Gift, 2, false)]
    [InlineData(MessageKind.Guard, 0, false)]
    [InlineData(MessageKind.Guard, 2, false)]
    [InlineData(MessageKind.SuperChat, 0, true)]
    [InlineData(MessageKind.SuperChat, 2, true)]
    [InlineData(MessageKind.Gift, 0, true)]
    [InlineData(MessageKind.Gift, 2, true)]
    [InlineData(MessageKind.Guard, 0, true)]
    [InlineData(MessageKind.Guard, 2, true)]
    public async Task ClickingUnreadMessagePreservesNearbyReadingPosition(MessageKind kind, int visibleIndex, bool startAtLatest)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), configuration);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (var index = 0; index < 120; index++)
        {
            var message = new MessageRecord
            {
                RoomTabId = "tab", RoomId = "123", Kind = kind, Time = time.AddSeconds(index),
                UserName = "viewer", Content = "message", GiftName = "gift", TotalPrice = 100, GuardLevel = 3
            };
            await runtime.Messages.AppendAsync(message, configuration.Settings.Reading);
            if (!startAtLatest && index >= 80)
            {
                await runtime.Messages.MarkReadAsync(message.Id, message.Quantity, message.TotalPrice);
            }
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 500, Height = 360, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "ScrollingMessages");
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.TemplatedParent == list);
            if (startAtLatest)
            {
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                Assert.False(view.Model.IsReadingHistory);
            }
            else
            {
                viewer.Offset = new(0, viewer.ScrollBarMaximum.Y / 3);
                Pump(window);
            }
            var visible = list.GetRealizedContainers()
                .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                    position.Y >= 0 && position.Y + container.Bounds.Height <= viewer.Viewport.Height)
                .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y)
                .ToArray();
            var clicked = visible[visibleIndex];
            var target = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(clicked));
            var retained = visible[visibleIndex == 0 ? 1 : 0];
            var neighbor = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(retained));
            var before = retained.TranslatePoint(default, viewer)!.Value.Y;
            var row = clicked.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, target));
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            for (var attempt = 0; attempt < 100 && !target.Record.IsRead; attempt++)
            {
                await Task.Delay(20);
                Pump(window);
            }
            Assert.True(target.Record.IsRead);
            Pump(window);
            Assert.False(target.Record.IsPriority);
            Assert.True(view.Model.IsReadingHistory);
            var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(neighbor));
            Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
            Assert.True(view.Model.ScrollingItems.IndexOf(target) > view.Model.ScrollingItems.IndexOf(neighbor));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
