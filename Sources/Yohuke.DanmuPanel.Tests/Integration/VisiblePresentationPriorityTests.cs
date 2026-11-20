using System.Collections.Concurrent;
using System.Threading.Channels;
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
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Translation;
using Yohuke.DanmuPanel.Tests.Windowing;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证可见行优先完成展示，并检验滞后的后台结果是否仍属于当前房间。</summary>
public sealed class VisiblePresentationPriorityTests
{
    private static readonly TimeSpan TEST_TIMEOUT = TimeSpan.FromSeconds(8);
    private readonly Channel<(string Text, TaskCompletionSource Release, Task Finished)> started =
        Channel.CreateUnbounded<(string, TaskCompletionSource, Task)>();
    private readonly TaskCompletionSource releaseAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> calls = new();
    private int activeCalls;
    private int peakActiveCalls;

    /// <summary>首批任务挂起时，普通行和置顶行的优先请求先于其他保留项，未注音时原文可读。</summary>
    [AvaloniaFact]
    public async Task PromotedScrollingAndPinnedRowsStartBeforeOtherRetainedRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), CreatePinyinService());
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, 64);
        await PinAsync(runtime, room, 12);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 600, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room).WaitAsync(TEST_TIMEOUT);
            var first = await FirstBatchAsync(runtime.Presentations.WorkerCount);
            var originalRows = view.Model.Items.ToArray();
            var scrolling = view.Model.ScrollingItems[^1];
            var pinned = view.Model.PinnedItems[^1];
            Assert.DoesNotContain(first, call => call.Text == scrolling.Record.Content || call.Text == pinned.Record.Content);
            Assert.True(originalRows.Length > first.Length + 2);

            view.Model.PrioritizePresentation(scrolling);
            view.Model.PrioritizePresentation(pinned);
            first[0].Release.SetResult();
            var next = await NextStartAsync();
            first[1].Release.SetResult();
            var afterNext = await NextStartAsync();

            Assert.Equal(new[] { scrolling.Record.Content, pinned.Record.Content }.Order(StringComparer.Ordinal),
                new[] { next.Text, afterNext.Text }.Order(StringComparer.Ordinal));
            Assert.Equal(runtime.Presentations.WorkerCount, Volatile.Read(ref activeCalls));

            window.Show();
            PinyinTextRenderingTests.Pump(window);
            AssertOriginalVisible(view, window, scrolling, "ScrollingMessages");
            AssertOriginalVisible(view, window, pinned, "PinnedMessages");
            Assert.Empty(scrolling.ContentPinyin);
            Assert.Empty(pinned.ContentPinyin);

            releaseAll.SetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TEST_TIMEOUT);

            Assert.Equal(originalRows, view.Model.Items);
            Assert.All(originalRows, item =>
            {
                Assert.False(item.IsPresentationPending);
                Assert.Single(item.ContentPinyin);
            });
            Assert.Equal(originalRows.Length, calls.Count);
            Assert.Equal(runtime.Presentations.WorkerCount, Volatile.Read(ref peakActiveCalls));
            Assert.Empty(errors);
        }
        finally
        {
            releaseAll.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>实际滚入两个列表各自视口的行会更新优先队列。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrollingIntoEitherRegionPromotesItsCurrentlyVisibleRows(bool pinned)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), CreatePinyinService());
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, 64);
        if (pinned)
        {
            await PinAsync(runtime, room, 64);
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 600, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room).WaitAsync(TEST_TIMEOUT);
            var first = await FirstBatchAsync(runtime.Presentations.WorkerCount);
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var region = pinned ? "PinnedMessages" : "ScrollingMessages";
            var list = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedListBox>(), control => control.Name == region);
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                control => control.TemplatedParent == viewer);
            var items = pinned ? view.Model.PinnedItems : view.Model.ScrollingItems;
            Assert.True(viewer.ScrollBarMaximum.Y > 0);
            var previouslyVisible = items.Where(list.IsMessageVisible).ToHashSet();
            Assert.NotEmpty(previouslyVisible);
            var entered = new HashSet<MessageItemViewModel>();
            var left = new HashSet<MessageItemViewModel>();
            list.MessageBecameVisible += item => entered.Add(item);
            list.MessageBecameHidden += item => left.Add(item);

            var middle = viewer.ScrollBarMaximum.Y / 2;
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                new Vector(0, middle - viewer.Offset.Y)));
            PinyinTextRenderingTests.Pump(window);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(previouslyVisible, item => left.Contains(item) && !list.IsMessageVisible(item));
            var startedTexts = first.Select(call => call.Text).ToHashSet(StringComparer.Ordinal);
            var queued = items.Where(item => !startedTexts.Contains(item.Record.Content)).ToArray();
            var visible = queued.Where(list.IsMessageVisible).ToArray();
            Assert.Contains(visible, item => entered.Contains(item) && !previouslyVisible.Contains(item));
            Assert.DoesNotContain(queued[0], visible);

            first[0].Release.SetResult();
            var next = await NextStartAsync();
            Assert.Contains(visible, item => item.Record.Content == next.Text);
            Assert.Empty(errors);
        }
        finally
        {
            releaseAll.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>切房时取消旧队列，已开始的高优先项完成后也不写回旧行或当前列表。</summary>
    [AvaloniaFact]
    public async Task SwitchingRoomsCancelsOldPrioritiesAndRejectsLateResults()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), CreatePinyinService());
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var other = new RoomTabState { Id = "other", RoomId = "456" };
        await SeedAsync(runtime, room, 24);
        await SeedAsync(runtime, other, 3);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        try
        {
            await model.SelectRoomAsync(room).WaitAsync(TEST_TIMEOUT);
            var first = await FirstBatchAsync(runtime.Presentations.WorkerCount);
            var inFlight = model.ScrollingItems[^1];
            var queued = model.ScrollingItems[^2];
            model.PrioritizePresentation(inFlight);
            first[0].Release.SetResult();
            var oldWork = await NextStartAsync();
            Assert.Equal(inFlight.Record.Content, oldWork.Text);
            model.PrioritizePresentation(queued);

            await model.SelectRoomAsync(other).WaitAsync(TEST_TIMEOUT);
            var current = model.ScrollingItems.ToArray();
            Assert.Equal(3, current.Length);
            Assert.All(current, item => Assert.True(item.ShowOriginal));
            releaseAll.SetResult();
            await oldWork.Finished.WaitAsync(TEST_TIMEOUT);
            await model.WaitForPresentationAsync().WaitAsync(TEST_TIMEOUT);

            Assert.Empty(inFlight.ContentPinyin);
            Assert.DoesNotContain(queued.Record.Content, calls);
            Assert.Equal(current, model.ScrollingItems);
            Assert.All(current, item =>
            {
                Assert.Equal(other.Id, item.Record.RoomTabId);
                Assert.Equal(item.Record.Content, Assert.Single(item.ContentPinyin).Text);
            });
            Assert.Equal(0, model.ViewportUpdateDepth);
        }
        finally
        {
            releaseAll.TrySetResult();
        }
    }

    private IPinyinService CreatePinyinService()
    {
        return new DelegatePinyinService(text => text, text =>
        {
            var active = Interlocked.Increment(ref activeCalls);
            var previousPeak = Volatile.Read(ref peakActiveCalls);
            while (active > previousPeak)
            {
                var observedPeak = Interlocked.CompareExchange(ref peakActiveCalls, active, previousPeak);
                if (observedPeak == previousPeak)
                {
                    break;
                }
                previousPeak = observedPeak;
            }

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Enqueue(text);
            started.Writer.TryWrite((text, release, finished.Task));
            try
            {
                Task.WhenAny(release.Task, releaseAll.Task).GetAwaiter().GetResult();
                return [new PinyinAnnotation(0, text, "zǎo")];
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
                finished.SetResult();
            }
        });
    }

    private Task<(string Text, TaskCompletionSource Release, Task Finished)> NextStartAsync()
    {
        return started.Reader.ReadAsync().AsTask().WaitAsync(TEST_TIMEOUT);
    }

    private async Task<(string Text, TaskCompletionSource Release, Task Finished)[]> FirstBatchAsync(int workers)
    {
        Assert.InRange(workers, 2, 4);
        var batch = new (string Text, TaskCompletionSource Release, Task Finished)[workers];
        for (var index = 0; index < batch.Length; index++)
        {
            batch[index] = await NextStartAsync();
        }
        Assert.Equal(workers, Volatile.Read(ref activeCalls));
        return batch;
    }

    private static void AssertOriginalVisible(MessageFeedView view, Window window, MessageItemViewModel item, string region)
    {
        var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == region);
        list.ScrollIntoView(item);
        PinyinTextRenderingTests.Pump(window);
        var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
        var container = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(item));
        var y = container.TranslatePoint(default, viewer)!.Value.Y;
        Assert.True(y + container.Bounds.Height > 0 && y < viewer.Viewport.Height);
        Assert.True(item.IsPresentationPending);
        Assert.Contains(container.Row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
            text => text.IsEffectivelyVisible && text.ContentText == item.Record.Content);
    }

    private static PanelConfiguration Configuration()
    {
        var configuration = new PanelConfiguration();
        configuration.Settings.ShowUserPinyin = false;
        configuration.Settings.ShowDanmuPinyin = true;
        configuration.Settings.ShowUserAvatars = false;
        configuration.Settings.AutoScrollLongMessages = false;
        configuration.Settings.Translation.Enabled = false;
        return configuration;
    }

    private static Task<int> SeedAsync(PanelRuntime runtime, RoomTabState room, int count)
    {
        var messages = Enumerable.Range(0, count).Select(index => new MessageRecord
        {
            Id = MessageId(room, index), RoomTabId = room.Id, RoomId = room.RoomId, Kind = MessageKind.Danmu,
            Content = $"早安 {room.Id} {index}", UserName = "viewer", IsTest = true,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        return runtime.Messages.AppendTestMessagesAsync(messages, runtime.Configuration.Settings.Reading);
    }

    private static string MessageId(RoomTabState room, int index)
    {
        return $"{room.Id}-{index}";
    }

    private static async Task PinAsync(PanelRuntime runtime, RoomTabState room, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await runtime.Messages.TogglePinAsync(MessageId(room, index));
        }
    }
}
