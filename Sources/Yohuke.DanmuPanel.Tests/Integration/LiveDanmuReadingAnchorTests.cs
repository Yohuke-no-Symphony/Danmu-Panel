using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Windowing;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证直播裁剪对实际阅读锚点、同毫秒顺序和回到最新的影响。</summary>
public sealed class LiveDanmuReadingAnchorTests
{
    private const int RETENTION = 240;
    private static readonly TimeSpan TEST_TIMEOUT = TimeSpan.FromSeconds(8);

    /// <summary>锚点仍在容量内就正常裁剪，下一批将移出它时固定裁剪前窗口。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveTrimmingPreservesTheVisibleHalfRowOrFreezesBeforeRemovingIt(bool overflow)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.MessageRetentionCount = RETENTION;
        settings.Translation.Enabled = false;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = false;
        settings.AutoScrollLongMessages = false;
        settings.ShowUserPinyin = false;
        settings.ShowDanmuPinyin = false;
        settings.ShowUserAvatars = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        await runtime.Messages.AppendTestMessagesAsync(Enumerable.Range(0, RETENTION)
            .Select(index => Message(index) with { IsTest = true }).ToArray(), settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "room", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleAsync(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                control => control.TemplatedParent == viewer);
            var height = FirstVisible(list, viewer).Height;
            var wanted = height * (overflow ? 160.5 : 40.5);
            ScrollBy(presenter, wanted - viewer.Offset.Y);
            await SettleAsync(window);
            var visible = FirstVisible(list, viewer);
            if (visible.Y >= -1 || visible.Y + visible.Height <= 1)
            {
                ScrollBy(presenter, visible.Y + visible.Height / 2);
                await SettleAsync(window);
                visible = FirstVisible(list, viewer);
            }
            Assert.InRange(visible.Y, -visible.Height + 1, -1);
            Assert.True(view.Model.IsReadingHistory);

            var originals = view.Model.ScrollingItems.ToArray();
            var countBeforeAnchor = Array.IndexOf(originals, visible.Item);
            Assert.InRange(countBeforeAnchor, overflow ? 129 : 1, overflow ? RETENTION - 1 : 128);
            var payloadReads = runtime.Messages.PayloadRowsRead;
            await PublishAsync(runtime, RETENTION, countBeforeAnchor);
            var lastFirstBatch = RETENTION + countBeforeAnchor - 1;
            await WaitForAsync(view.Model, () => view.Model.ScrollingItems[^1].Record.Id == MessageId(lastFirstBatch));
            await SettleAsync(window);

            Assert.False(view.Model.HasNewerHistory);
            Assert.Equal(RETENTION, view.Model.ScrollingItems.Count);
            Assert.Equal(originals.Skip(countBeforeAnchor), view.Model.ScrollingItems.Take(RETENTION - countBeforeAnchor));
            Assert.Equal(Enumerable.Range(countBeforeAnchor, RETENTION).Select(MessageId),
                view.Model.ScrollingItems.Select(item => item.Record.Id));
            AssertAnchor();
            if (overflow)
            {
                Assert.True(runtime.Messages.PayloadRowsRead > payloadReads);
            }
            else
            {
                Assert.Equal(payloadReads, runtime.Messages.PayloadRowsRead);
            }

            var beforeFreeze = view.Model.ScrollingItems.ToArray();
            var windowChanges = 0;
            NotifyCollectionChangedEventHandler onRowsChanged = (_, _) => windowChanges++;
            view.Model.ScrollingRows.CollectionChanged += onRowsChanged;
            var nextCount = overflow ? 129 : 1;
            await PublishAsync(runtime, lastFirstBatch + 1, nextCount);
            await WaitForAsync(view.Model, () => view.Model.HasNewerHistory);
            await SettleAsync(window);
            view.Model.ScrollingRows.CollectionChanged -= onRowsChanged;

            Assert.Equal(0, windowChanges);
            Assert.Equal(beforeFreeze, view.Model.ScrollingItems);
            AssertAnchor();
            Assert.True(view.Model.IsReadingHistory);

            await view.Header.BackToLatestCommand.ExecuteAsync(null).WaitAsync(TEST_TIMEOUT);
            await view.Model.WaitForPresentationAsync().WaitAsync(TEST_TIMEOUT);
            await SettleAsync(window);

            Assert.Equal(Enumerable.Range(countBeforeAnchor + nextCount, RETENTION).Select(MessageId),
                view.Model.ScrollingItems.Select(item => item.Record.Id));
            Assert.False(view.Model.HasNewerHistory);
            Assert.False(view.Model.IsReadingHistory);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            Assert.Empty(errors);

            void AssertAnchor()
            {
                var retained = FirstVisible(list, viewer);
                Assert.Same(visible.Item, retained.Item);
                Assert.InRange(Math.Abs(visible.Y - retained.Y), 0, 1);
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>滚到历史窗口底部时补齐最新记录，随后继续接收实时弹幕。</summary>
    [AvaloniaFact]
    public async Task ScrollingToTheBottomOfAnOlderPageRestoresLiveDanmu()
    {
        const int LIVE_COUNT = 100;
        const int HISTORY_COUNT = LIVE_COUNT + 400;
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.MessageRetentionCount = LIVE_COUNT;
        settings.Translation.Enabled = false;
        settings.CompactDanmuDisplay = true;
        settings.ShowUserPinyin = false;
        settings.ShowDanmuPinyin = false;
        settings.ShowUserAvatars = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        await runtime.Messages.AppendTestMessagesAsync(Enumerable.Range(0, HISTORY_COUNT)
            .Select(index => Message(index) with { IsTest = true }).ToArray(), settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var model = view.Model;
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await model.SelectRoomAsync(new() { Id = "room", RoomId = "123" });
            window.Show();
            await SettleAsync(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                control => control.TemplatedParent == viewer);

            ScrollBy(presenter, -viewer.Offset.Y - 20);
            await WaitForAsync(model, () => !model.IsLoadingHistory && model.ScrollingItems.Count == LIVE_COUNT + 100);
            for (var page = 0; page < 3; page++)
            {
                await model.LoadMoreAsync().WaitAsync(TEST_TIMEOUT);
            }
            await SettleAsync(window);
            Assert.True(model.IsReadingHistory);
            Assert.True(model.HasNewerHistory);
            Assert.Equal(MessageId(HISTORY_COUNT - 101), model.ScrollingItems[^1].Record.Id);

            await PublishAsync(runtime, HISTORY_COUNT, 1);
            ScrollBy(presenter, viewer.ScrollBarMaximum.Y - viewer.Offset.Y + 20);
            await WaitForAsync(model, () => !model.HasNewerHistory && model.ScrollingItems[^1].Record.Id == MessageId(HISTORY_COUNT));
            await SettleAsync(window);
            Assert.Equal(Enumerable.Range(HISTORY_COUNT - LIVE_COUNT + 1, LIVE_COUNT).Select(MessageId),
                model.ScrollingItems.Select(item => item.Record.Id));
            Assert.False(model.IsReadingHistory);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);

            await PublishAsync(runtime, HISTORY_COUNT + 1, 1);
            await WaitForAsync(model, () => model.ScrollingItems[^1].Record.Id == MessageId(HISTORY_COUNT + 1));
            await SettleAsync(window);
            Assert.Equal(LIVE_COUNT, model.ScrollingItems.Count);
            Assert.False(model.HasNewerHistory);
            Assert.False(model.IsReadingHistory);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            Assert.Empty(errors);
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
    }

    private static (MessageItemViewModel Item, double Y, double Height) FirstVisible(ListBox list, ScrollViewer viewer)
    {
        var first = list.GetRealizedContainers()
            .Where(container => container.IsEffectivelyVisible && list.ItemFromContainer(container) is MessageItemViewModel)
            .Select(container => (Container: container, Y: container.TranslatePoint(default, viewer)!.Value.Y))
            .Where(value => value.Y < viewer.Viewport.Height && value.Y + value.Container.Bounds.Height > 0)
            .MinBy(value => value.Y);
        var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(first.Container));
        return (item, first.Y, first.Container.Bounds.Height);
    }

    private static void ScrollBy(ScrollContentPresenter presenter, double delta)
    {
        var gesture = ScrollGestureEventArgs.GetNextFreeId();
        presenter.RaiseEvent(new ScrollGestureEventArgs(gesture, new(0, delta)));
        presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gesture));
    }

    private static async Task PublishAsync(PanelRuntime runtime, int start, int count)
    {
        var committed = new List<MessageRecord>(count);
        foreach (var batch in Enumerable.Range(start, count).Select(Message).Chunk(IMessageStore.MAX_BATCH_COUNT))
        {
            var results = await runtime.Messages.AppendReceivedBatchAsync(batch);
            committed.AddRange(results.Select(result => Assert.IsType<MessageRecord>(result.Record)));
        }
        foreach (var record in committed)
        {
            runtime.PublishCommittedMessage(record);
        }
    }

    private static MessageRecord Message(int index)
    {
        return new()
        {
            Id = MessageId(index), RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu,
            UserName = "viewer", Content = $"消息 {index}", Time = DateTimeOffset.FromUnixTimeSeconds(1800000000)
        };
    }

    private static string MessageId(int index)
    {
        return $"live-{index}";
    }

    private static async Task WaitForAsync(FeedViewModel model, Func<bool> condition)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyCollectionChangedEventHandler rowsChanged = (_, _) => Check();
        PropertyChangedEventHandler stateChanged = (_, _) => Check();
        model.ScrollingItems.CollectionChanged += rowsChanged;
        model.PropertyChanged += stateChanged;
        try
        {
            Check();
            await completed.Task.WaitAsync(TEST_TIMEOUT);
        }
        finally
        {
            model.ScrollingItems.CollectionChanged -= rowsChanged;
            model.PropertyChanged -= stateChanged;
        }

        void Check()
        {
            if (condition())
            {
                completed.TrySetResult();
            }
        }
    }

    private static async Task SettleAsync(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            PinyinTextRenderingTests.Pump(window);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
    }
}
