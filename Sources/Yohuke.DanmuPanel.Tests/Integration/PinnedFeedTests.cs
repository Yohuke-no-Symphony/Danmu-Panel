using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证弹幕固定置顶、其他消息排序置顶与普通历史窗口。</summary>
public sealed class PinnedFeedTests
{
    /// <summary>弹幕置顶和取消置顶复用行模型，其他消息确认后移入独立已读窗口。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task PinningMovesDanmuAndReadingMovesPaidMessagesBetweenTabs(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 30 };
        runtime.Configuration.Settings.Reading = options;
        var target = Message(kind, 1);
        var normal = Message(kind, 0, kind == MessageKind.Gift ? 30 : 10);
        await runtime.Messages.AppendAsync(target, options);
        await runtime.Messages.AppendAsync(normal, options);
        if (kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard)
        {
            await runtime.Messages.MarkReadAsync(normal.Id, normal.Quantity, normal.TotalPrice);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 700, Height = 480, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            window.Show();
            Pump(window);
            var targetItem = view.Model.Items.Single(item => item.Record.Id == target.Id);
            var normalItem = kind == MessageKind.Danmu
                ? view.Model.Items.Single(item => item.Record.Id == normal.Id) : null;
            if (kind == MessageKind.Danmu)
            {
                Assert.Empty(view.Model.PinnedItems);
                FindRow(FindList(view, "ScrollingMessages"), target.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(async () => (await runtime.Messages.GetByIdAsync(target.Id))!.IsPinned);
                await view.Model.RefreshAsync();
                Pump(window);
            }

            if (kind == MessageKind.Danmu)
            {
                Assert.Same(targetItem, Assert.Single(view.Model.PinnedItems));
                Assert.Same(normalItem, Assert.Single(view.Model.ScrollingItems));
                Assert.Same(view.Model.PinnedItems, FindList(view, "PinnedMessages").ItemsSource);
            }
            else
            {
                Assert.Empty(view.Model.PinnedItems);
                Assert.False(view.Model.HasPinnedItems);
                Assert.Same(targetItem, Assert.Single(view.Model.ScrollingItems));
                Assert.DoesNotContain(view.Model.Items, item => item.Record.Id == normal.Id);
            }
            Assert.Same(view.Model.ScrollingRows, FindList(view, "ScrollingMessages").ItemsSource);
            Assert.Equal(view.Model.ScrollingItems, view.Model.ScrollingRows.OfType<MessageItemViewModel>());
            Assert.True(targetItem.Record.IsPriority);
            var targetList = FindList(view, kind == MessageKind.Danmu ? "PinnedMessages" : "ScrollingMessages");
            FindRow(targetList, target.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(async () => !(await runtime.Messages.GetByIdAsync(target.Id))!.IsPriority);
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Empty(view.Model.PinnedItems);
            Assert.False(view.Model.HasPinnedItems);
            if (kind == MessageKind.Danmu)
            {
                Assert.Equal(2, view.Model.ScrollingItems.Count);
                Assert.Same(normalItem, view.Model.ScrollingItems[0]);
                Assert.Same(targetItem, view.Model.ScrollingItems[1]);
            }
            else
            {
                Assert.Empty(view.Model.ScrollingItems);
                await view.Model.SelectReadFilterAsync(MessageReadFilter.Read);
                Assert.Equal(new[] { normal.Id, target.Id }, view.Model.ScrollingItems.Select(item => item.Record.Id));
                Assert.All(view.Model.ScrollingItems, item => Assert.False(item.ShowUnreadDot));
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>阅读普通历史时，置顶消息的窗口坐标与其自身滚动位置保持不变。</summary>
    [AvaloniaFact]
    public async Task ScrollingHistoryDoesNotMovePinnedRegion()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var pinned = Message(MessageKind.Danmu, -1);
        await runtime.Messages.AppendAsync(pinned, new());
        await runtime.Messages.TogglePinAsync(pinned.Id);
        await AppendDanmuAsync(runtime, 0, 40);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 700, Height = 480, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            window.Show();
            Pump(window);
            var pinnedList = FindList(view, "PinnedMessages");
            var normalList = FindList(view, "ScrollingMessages");
            var pinnedViewer = FindScroll(pinnedList);
            var normalViewer = FindScroll(normalList);
            var pinnedRow = FindRow(pinnedList, pinned.Id);
            await view.Model.WaitForPresentationAsync();
            var before = pinnedRow.TranslatePoint(new Point(0, 0), window)!.Value;
            var pinnedOffset = pinnedViewer.Offset;

            var presenter = Assert.Single(normalViewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, normalViewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                new(0, normalViewer.ScrollBarMaximum.Y * 0.25 - normalViewer.Offset.Y)));
            Pump(window);

            Assert.True(normalViewer.Offset.Y > 0);
            Assert.True(normalViewer.Offset.Y < normalViewer.ScrollBarMaximum.Y);
            Assert.True(view.Model.IsReadingHistory);
            var after = pinnedRow.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.InRange(Math.Abs(before.X - after.X), 0, 0.5);
            Assert.InRange(Math.Abs(before.Y - after.Y), 0, 0.5);
            Assert.Equal(pinnedOffset, pinnedViewer.Offset);
            Assert.True(pinnedRow.IsEffectivelyVisible);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>置顶内容超过可用高度时独立滚动，普通历史仍有可阅读的空间。</summary>
    [AvaloniaFact]
    public async Task LargePinnedRegionScrollsIndependentlyAndReservesNormalSpace()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        for (var index = 0; index < 30; index++)
        {
            var message = Message(MessageKind.Danmu, index);
            await runtime.Messages.AppendAsync(message, new());
            await runtime.Messages.TogglePinAsync(message.Id);
        }
        await AppendDanmuAsync(runtime, 30, 30);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 700, Height = 500, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            window.Show();
            Pump(window);
            var pinnedList = FindList(view, "PinnedMessages");
            var normalList = FindList(view, "ScrollingMessages");
            var pinnedViewer = FindScroll(pinnedList);
            var normalViewer = FindScroll(normalList);
            Assert.True(pinnedList.Bounds.Height > 0);
            Assert.True(normalList.Bounds.Height >= view.Bounds.Height / 2);
            Assert.True(pinnedViewer.Extent.Height > pinnedViewer.Viewport.Height);
            var normalOffset = normalViewer.Offset;
            var normalPosition = normalList.TranslatePoint(new Point(0, 0), window)!.Value;

            pinnedList.ScrollIntoView(view.Model.PinnedItems.Last());
            Pump(window);

            Assert.True(pinnedViewer.Offset.Y > 0);
            Assert.Equal(normalOffset, normalViewer.Offset);
            Assert.Equal(normalPosition, normalList.TranslatePoint(new Point(0, 0), window)!.Value);
            Assert.True(normalList.IsEffectivelyVisible);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>未读窗口的消息共同滚动，不会固定在面板顶部。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task UnreadMessagesScrollWithoutPinnedRegion(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var priority = Message(kind, -1);
        await runtime.Messages.AppendAsync(priority, new());
        for (var index = 0; index < 40; index++)
        {
            var record = Message(kind, index);
            await runtime.Messages.AppendAsync(record, new());
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 700, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            window.Show();
            Pump(window);
            Assert.Empty(view.Model.PinnedItems);
            Assert.Equal(41, view.Model.ScrollingItems.Count);
            var first = view.Model.ScrollingItems[0];
            Assert.Equal(priority.Id, first.Record.Id);
            var list = FindList(view, "ScrollingMessages");
            var viewer = FindScroll(list);
            Assert.InRange(list.Bounds.Height, view.Bounds.Height - 20, view.Bounds.Height);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<ListBox>(),
                value => value.Name == "PinnedMessages" && value.IsEffectivelyVisible);

            await view.Model.WaitForPresentationAsync();
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                new(0, viewer.ScrollBarMaximum.Y * 0.5 - viewer.Offset.Y)));
            Pump(window);

            Assert.True(viewer.Offset.Y > 0);
            Assert.True(view.Model.IsReadingHistory);
            if (list.ContainerFromItem(first) is { } container)
            {
                Assert.True(container.TranslatePoint(default, viewer)!.Value.Y + container.Bounds.Height <= 0);
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>完整未读窗口不占已读分页容量，已读刷新保留边界，加载更多增加一百条历史。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task UnreadMessagesDoNotConsumeReadHistoryPages(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        for (var index = 0; index < 310; index++)
        {
            var record = Message(kind, index);
            await runtime.Messages.AppendAsync(record, new());
            await runtime.Messages.MarkReadAsync(record.Id, 1, record.TotalPrice);
        }
        for (var index = 310; index < 415; index++)
        {
            await runtime.Messages.AppendAsync(Message(kind, index), new());
        }
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(Room());

        Assert.Empty(model.PinnedItems);
        Assert.Equal(105, model.ScrollingItems.Count);
        Assert.All(model.ScrollingItems, item => Assert.False(item.Record.IsRead));
        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        Assert.Equal(100, model.Items.Count);
        Assert.All(model.Items, item => Assert.True(item.Record.IsRead));
        var oldest = model.ScrollingItems.First(item => !item.Record.IsPriority);
        model.IsReadingHistory = true;
        var incoming = Message(kind, 1000);
        await runtime.Messages.AppendAsync(incoming, new());
        await runtime.Messages.MarkReadAsync(incoming.Id, incoming.Quantity, incoming.TotalPrice);
        await runtime.Messages.AppendAsync(Message(kind, 1001), new());
        await model.RefreshAsync();

        Assert.Equal(106, model.UnreadCount);
        Assert.DoesNotContain(model.ScrollingItems, item => item.Record.IsPriority);
        Assert.Equal(101, model.ScrollingItems.Count(item => !item.Record.IsPriority));
        Assert.Same(oldest, model.ScrollingItems.First(item => !item.Record.IsPriority));

        await model.LoadMoreAsync();

        Assert.Equal(106, model.UnreadCount);
        Assert.Equal(201, model.ScrollingItems.Count(item => !item.Record.IsPriority));
        Assert.Same(oldest, model.ScrollingItems[100]);
        await model.SelectReadFilterAsync(MessageReadFilter.Unread);
        Assert.Equal(106, model.Items.Count);
        Assert.All(model.Items, item => Assert.False(item.Record.IsRead));
    }

    /// <summary>读到普通列表尾部后接收新消息，保留最老行实例，加载更多再追加一百条。</summary>
    [AvaloniaFact]
    public async Task ReadingHistoryKeepsOldestViewModelAndLoadMoreAddsOneHundred()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await AppendDanmuAsync(runtime, 0, 310);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 700, Height = 480, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            window.Show();
            Pump(window);
            Assert.Equal(100, view.Model.ScrollingItems.Count);
            var initial = view.Model.ScrollingItems.ToArray();
            var oldest = initial[0];
            var normalList = FindList(view, "ScrollingMessages");
            await view.Model.WaitForPresentationAsync();
            var viewer = FindScroll(normalList);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, -viewer.Offset.Y)));
            Pump(window);
            Assert.Equal(0, FindScroll(normalList).Offset.Y);
            Assert.True(view.Model.IsReadingHistory);

            await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, 1000), new());
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Equal(101, view.Model.ScrollingItems.Count);
            Assert.Same(oldest, view.Model.ScrollingItems[0]);
            Assert.All(initial, item => Assert.Same(item,
                view.Model.ScrollingItems.Single(current => current.Record.Id == item.Record.Id)));
            Assert.Equal(0, FindScroll(normalList).Offset.Y);

            await view.Model.LoadMoreAsync();
            Pump(window);

            Assert.Equal(201, view.Model.ScrollingItems.Count);
            Assert.Same(oldest, view.Model.ScrollingItems[100]);
            Assert.All(initial, item => Assert.Same(item,
                view.Model.ScrollingItems.Single(current => current.Record.Id == item.Record.Id)));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>停留底部跟随消息时仍保持一页，旧条目不会因持续接收无限累积。</summary>
    [AvaloniaFact]
    public async Task FollowingLatestKeepsNormalWindowAtOneHundred()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await AppendDanmuAsync(runtime, 0, 120);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(Room());
        var latest = model.ScrollingItems[^1];
        var oldest = model.ScrollingItems.First(item => !item.Record.IsPriority);
        Assert.False(model.IsReadingHistory);

        await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, 1000), new());
        await model.RefreshAsync();

        Assert.Equal(100, model.ScrollingItems.Count);
        Assert.Same(latest, model.ScrollingItems[^2]);
        Assert.DoesNotContain(oldest, model.ScrollingItems);
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new ConfigurationService(directory.Path), new PanelConfiguration
        {
            Settings = new() { MessageRetentionCount = 100 }
        });
    }

    private static RoomTabState Room()
    {
        return new() { Id = "tab", RoomId = "123" };
    }

    private static MessageRecord Message(MessageKind kind, int sequence, decimal price = 30)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, Content = "消息 " + sequence,
            UserName = "viewer", UserUid = 42, GiftName = "鲜花", GiftId = "flower",
            UnitPrice = price, TotalPrice = price, GuardLevel = 3,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + sequence)
        };
    }

    private static async Task AppendDanmuAsync(PanelRuntime runtime, int start, int count)
    {
        for (var index = start; index < start + count; index++)
        {
            await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, index), new());
        }
    }

    private static ListBox FindList(MessageFeedView view, string name)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), list => list.Name == name);
    }

    private static ScrollViewer FindScroll(ListBox list)
    {
        return Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), viewer => viewer.TemplatedParent == list);
    }

    private static Button FindRow(ListBox list, string id)
    {
        return list.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The message did not leave or enter the pinned region within the test timeout.");
    }
}
