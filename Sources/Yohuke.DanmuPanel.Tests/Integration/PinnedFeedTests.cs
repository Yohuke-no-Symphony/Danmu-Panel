using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

/// <summary>验证固定置顶区域、独立滚动与普通历史窗口。</summary>
public sealed class PinnedFeedTests
{
    /// <summary>四类消息按各自规则进入固定区，解除优先状态时复用原来的行模型。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task PriorityAndNormalRegionsMoveExistingViewModels(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 30 };
        runtime.Configuration.Settings.Reading = options;
        var target = Message(kind, 0, 30);
        var normal = Message(kind, 1, 10);
        await runtime.Messages.AppendAsync(target, options);
        await runtime.Messages.AppendAsync(normal, options);
        if (kind is MessageKind.SuperChat or MessageKind.Guard)
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
            var normalItem = view.Model.Items.Single(item => item.Record.Id == normal.Id);
            if (kind == MessageKind.Danmu)
            {
                Assert.Empty(view.Model.PinnedItems);
                FindRow(FindList(view, "ScrollingMessages"), target.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(async () => (await runtime.Messages.GetByIdAsync(target.Id))!.IsPinned);
                await view.Model.RefreshAsync();
                Pump(window);
            }

            Assert.Same(targetItem, Assert.Single(view.Model.PinnedItems));
            Assert.Same(normalItem, Assert.Single(view.Model.ScrollingItems));
            Assert.Same(view.Model.PinnedItems, FindList(view, "PinnedMessages").ItemsSource);
            Assert.Same(view.Model.ScrollingItems, FindList(view, "ScrollingMessages").ItemsSource);
            Assert.True(targetItem.Record.IsPriority);
            if (kind == MessageKind.Gift)
            {
                Assert.False(normalItem.Record.IsRead);
                Assert.False(normalItem.Record.IsPriority);
            }

            FindRow(FindList(view, "PinnedMessages"), target.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(async () => !(await runtime.Messages.GetByIdAsync(target.Id))!.IsPriority);
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Empty(view.Model.PinnedItems);
            Assert.Equal(2, view.Model.ScrollingItems.Count);
            Assert.Same(normalItem, view.Model.ScrollingItems[0]);
            Assert.Same(targetItem, view.Model.ScrollingItems[1]);
            Assert.False(view.Model.HasPinnedItems);
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
            var before = pinnedRow.TranslatePoint(new Point(0, 0), window)!.Value;
            var pinnedOffset = pinnedViewer.Offset;

            normalList.ScrollIntoView(view.Model.ScrollingItems.Last());
            Pump(window);

            Assert.True(normalViewer.Offset.Y > 0);
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

    /// <summary>全部未读 SC 超过一页时，不挤掉普通历史的一百条容量。</summary>
    [AvaloniaFact]
    public async Task MoreThanOneHundredPriorityMessagesDoNotConsumeNormalPage()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        for (var index = 0; index < 110; index++)
        {
            var record = Message(MessageKind.SuperChat, index);
            await runtime.Messages.AppendAsync(record, new());
            await runtime.Messages.MarkReadAsync(record.Id, 1, record.TotalPrice);
        }
        for (var index = 110; index < 215; index++)
        {
            await runtime.Messages.AppendAsync(Message(MessageKind.SuperChat, index), new());
        }
        using var model = new FeedViewModel(runtime, MessageKind.SuperChat);
        await model.SelectRoomAsync(Room());

        Assert.Equal(105, model.PinnedItems.Count);
        Assert.Equal(100, model.ScrollingItems.Count);
        Assert.All(model.PinnedItems, item => Assert.False(item.Record.IsRead));
        Assert.All(model.ScrollingItems, item => Assert.True(item.Record.IsRead));
        Assert.Equal(205, model.Items.Count);
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
            var oldest = initial[^1];
            var normalList = FindList(view, "ScrollingMessages");
            normalList.ScrollIntoView(oldest);
            Pump(window);
            Assert.True(FindScroll(normalList).Offset.Y > 0);
            Assert.True(view.Model.IsReadingHistory);

            await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, 1000), new());
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Equal(101, view.Model.ScrollingItems.Count);
            Assert.Same(oldest, view.Model.ScrollingItems.Last());
            Assert.All(initial, item => Assert.Same(item,
                view.Model.ScrollingItems.Single(current => current.Record.Id == item.Record.Id)));
            Assert.True(FindScroll(normalList).Offset.Y > 0);

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

    /// <summary>停留顶部跟随消息时仍保持一页，旧条目不会因持续接收无限累积。</summary>
    [AvaloniaFact]
    public async Task FollowingLatestKeepsNormalWindowAtOneHundred()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await AppendDanmuAsync(runtime, 0, 120);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(Room());
        var first = model.ScrollingItems[0];
        var oldest = model.ScrollingItems.Last();
        Assert.False(model.IsReadingHistory);

        await runtime.Messages.AppendAsync(Message(MessageKind.Danmu, 1000), new());
        await model.RefreshAsync();

        Assert.Equal(100, model.ScrollingItems.Count);
        Assert.Same(first, model.ScrollingItems[1]);
        Assert.DoesNotContain(oldest, model.ScrollingItems);
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new ConfigurationService(directory.Path), new PanelConfiguration());
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
