using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证点击已读移出未读窗口时保留当前阅读位置。</summary>
public sealed class MessageReadScrollTests
{
    /// <summary>点击视口顶部或中部消息后，固定最顶部可见且未被点击的消息；半行也作为阅读锚点。</summary>
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
                var actions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
                var bottom = actions.Children.OfType<Button>().Single(button => button.Name == "BackToLatestButton");
                await Assert.IsAssignableFrom<IAsyncRelayCommand>(bottom.Command).ExecuteAsync(null);
                Pump(window);
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                Assert.False(view.Model.IsReadingHistory);
            }
            else
            {
                var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
                presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                    new(0, viewer.ScrollBarMaximum.Y / 3 - viewer.Offset.Y)));
                Pump(window);
                Assert.True(viewer.Offset.Y > 0 && viewer.Offset.Y < viewer.ScrollBarMaximum.Y);
            }
            var visible = list.GetRealizedContainers()
                .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                    position.Y >= 0 && position.Y + container.Bounds.Height <= viewer.Viewport.Height)
                .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y)
                .ToArray();
            var clicked = visible[visibleIndex];
            var target = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(clicked));
            var retained = list.GetRealizedContainers()
                .Where(container => container.IsEffectivelyVisible &&
                    list.ItemFromContainer(container) is MessageItemViewModel actual && !ReferenceEquals(actual, target) &&
                    container.TranslatePoint(default, viewer) is { } position && position.Y < viewer.Viewport.Height &&
                    position.Y + container.Bounds.Height > 0)
                .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
            var neighbor = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(retained));
            var before = retained.TranslatePoint(default, viewer)!.Value.Y;
            var neighborContentY = before + viewer.Offset.Y;
            var removedHeight = clicked.Bounds.Height;
            var removedAboveNeighbor = clicked.TranslatePoint(default, viewer)!.Value.Y < before;
            var row = clicked.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, target));
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            for (var attempt = 0; attempt < 100 && view.Model.Items.Contains(target); attempt++)
            {
                await Task.Delay(20);
                Pump(window);
            }
            Assert.DoesNotContain(target, view.Model.Items);
            Assert.True((await runtime.Messages.GetByIdAsync(target.Record.Id))!.IsRead);
            Pump(window);
            Assert.True(view.Model.IsReadingHistory);
            var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(neighbor));
            var contentY = neighborContentY - (removedAboveNeighbor ? removedHeight : 0);
            var expectedOffset = Math.Clamp(contentY - before, 0, viewer.ScrollBarMaximum.Y);
            var expectedY = contentY - expectedOffset;
            Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - expectedY), 0, 1);
            Assert.DoesNotContain(target, view.Model.ScrollingItems);
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
