using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

/// <summary>验证延迟挂载、取消和观察者异常不会遗留滚动更新作用域。</summary>
public sealed class FeedViewportLifecycleTests
{
    /// <summary>已脱离视图的挂载回调不能重置阅读状态，重新挂载后仍能加载及稳定阅读。</summary>
    [AvaloniaFact]
    public async Task QueuedAttachmentDoesNotReactivateDetachedViewAndReattachmentRemainsUsable()
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "你好历史", Count = 120 });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 600, Height = 320, Content = view };
        var starts = 0;
        var completions = 0;
        view.Model.ViewportUpdateStarted += () => starts++;
        view.Model.ViewportUpdateCompleted += () => completions++;
        try
        {
            window.Show();
            window.Content = null;
            view.Model.IsReadingHistory = true;

            Pump(window);

            Assert.True(view.Model.IsReadingHistory);
            window.Content = view;
            await view.Model.SelectRoomAsync(Room()).WaitAsync(TimeSpan.FromSeconds(5));
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(view.Model.ViewportUpdateDepth > 0);
            window.Content = null;
            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, view.Model.ViewportUpdateDepth);
            Assert.Equal(starts, completions);

            window.Content = view;
            Pump(window);
            await view.Model.LoadMoreAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Pump(window);
            Assert.Equal(120, view.Model.ScrollingItems.Count);
            Assert.Equal(0, view.Model.ViewportUpdateDepth);
            Assert.Equal(starts, completions);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), value => value.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), value => value.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                value => ReferenceEquals(value.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new(0, -viewer.Offset.Y)));
            Pump(window);
            Assert.True(view.Model.IsReadingHistory);
            Assert.InRange(Math.Abs(viewer.Offset.Y), 0, 1);

            await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "你好新消息" });
            await view.Model.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Pump(window);

            Assert.True(view.Model.IsReadingHistory);
            Assert.InRange(Math.Abs(viewer.Offset.Y), 0, 1);
            Assert.Equal(0, view.Model.ViewportUpdateDepth);
            Assert.Equal(starts, completions);
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>UI 批次让出期间关闭模型后，剩余旧行停止应用且作用域全部完成。</summary>
    [AvaloniaFact]
    public async Task DisposingDuringHistoryUiYieldBalancesViewportScopes()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "历史", Count = 150 });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var starts = 0;
        var completions = 0;
        var countAtDispose = 0;
        var disposed = false;
        model.ViewportUpdateStarted += () => starts++;
        model.ViewportUpdateCompleted += () => completions++;
        model.Items.CollectionChanged += (_, args) =>
        {
            if (!disposed && args.Action == NotifyCollectionChangedAction.Add && model.Items.Count == 20)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    countAtDispose = model.Items.Count;
                    disposed = true;
                    model.Dispose();
                }, DispatcherPriority.Input);
            }
        };

        await model.SelectRoomAsync(Room()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(disposed);
        Assert.Equal(20, countAtDispose);
        Assert.Equal(countAtDispose, model.Items.Count);
        Assert.False(model.IsLoadingHistory);
        Assert.DoesNotContain(model.ScrollingRows, value => value is MessageLoadingPlaceholderViewModel);
        Assert.Equal(0, model.ViewportUpdateDepth);
        Assert.Equal(starts, completions);
    }

    /// <summary>切房间取消旧拼音等待后，旧作用域和新作用域最终各自完成且不回写旧行。</summary>
    [AvaloniaFact]
    public async Task SwitchingRoomsCancelsTheOldPresentationScope()
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        var other = new RoomTabState { Id = "other", RoomId = "456" };
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "你好旧消息", Count = 3 });
        await runtime.TestMessages.AddAsync(other, new() { UserName = "viewer", Content = "你好新房间", Count = 3 });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var starts = 0;
        var completions = 0;
        model.ViewportUpdateStarted += () => starts++;
        model.ViewportUpdateCompleted += () => completions++;
        try
        {
            await model.SelectRoomAsync(Room()).WaitAsync(TimeSpan.FromSeconds(5));
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.SelectRoomAsync(other).WaitAsync(TimeSpan.FromSeconds(5));
            pinyin.Release.TrySetResult();
            await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(3, model.ScrollingItems.Count);
            Assert.All(model.ScrollingItems, item => Assert.Equal("other", item.Record.RoomTabId));
            Assert.Equal(0, model.ViewportUpdateDepth);
            Assert.Equal(starts, completions);
            Assert.False(model.IsLoadingHistory);
        }
        finally
        {
            pinyin.Release.TrySetResult();
        }
    }

    /// <summary>开始或结束布局通知抛出后仍释放刷新锁，下一次请求可正常完成。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingViewportObserverDoesNotPoisonTheNextRefresh(bool throwOnCompletion)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "history", Count = 3 });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        Action fail = () => throw new InvalidOperationException("viewport observer fixture");
        if (throwOnCompletion)
        {
            model.ViewportUpdateCompleted += fail;
        }
        else
        {
            model.ViewportUpdateStarted += fail;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.SelectRoomAsync(Room()));
        model.ViewportUpdateCompleted -= fail;
        model.ViewportUpdateStarted -= fail;

        await model.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, model.ScrollingItems.Count);
        Assert.Equal(0, model.ViewportUpdateDepth);
        Assert.False(model.IsLoadingHistory);
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }
    }

    private static PanelConfiguration Configuration()
    {
        return new() { Settings = new() { ShowUserPinyin = true, ShowDanmuPinyin = true } };
    }

    private static RoomTabState Room()
    {
        return new() { Id = "tab", RoomId = "123" };
    }
}
