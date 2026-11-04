using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>使用真实虚拟化列表验证输入滚动与原生阅读锚点互不竞争。</summary>
public sealed class FeedScrollStabilityTests
{
    private static readonly IPinyinService pinyin = new PinyinService();

    /// <summary>首次连接与底部追加在渲染前完成跟随，内容变高后仍贴住最新位置。</summary>
    [AvaloniaFact]
    public void LatestPositionIsResolvedBeforeRendering()
    {
        var (window, list, viewer, controller, items) = CreateFeed(startAtLatest: true);
        using (controller)
        {
            try
            {
                AssertAtLatest(viewer);
                Assert.True(controller.IsFollowingLatest);
                for (var index = 0; index < 50; index++)
                {
                    items.Add(CreateItem($"arrived {index}"));
                }
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                window.UpdateLayout();
                AssertAtLatest(viewer);
                Assert.NotNull(list.ContainerFromItem(items[^1]));

                var last = items[^1];
                last.Update(last.Record with { Content = string.Concat(Enumerable.Repeat("a taller translated body ", 40)) }, new());
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                window.UpdateLayout();
                AssertAtLatest(viewer);
                Assert.True(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>容器被批量插入回收时，阅读锚点必须在渲染前恢复，避免出现错位帧。</summary>
    [AvaloniaFact]
    public void RecycledReadingAnchorIsRestoredBeforeRendering()
    {
        var (window, list, viewer, controller, items) = CreateFeed(0);
        using (controller)
        {
            try
            {
                viewer.Offset = new(0, 650);
                Pump(window);
                var anchor = Assert.IsAssignableFrom<Control>(viewer.CurrentAnchor);
                var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
                var before = anchor.TranslatePoint(default, viewer)!.Value.Y;
                for (var index = 0; index < 500; index++)
                {
                    items.Insert(0, CreateItem($"older history {index}"));
                }
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                window.UpdateLayout();

                var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
                Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>连续滚轮向下始终前进，静态布局不会恢复旧的阅读偏移。</summary>
    [AvaloniaFact]
    public void RepeatedWheelInputAndStaticLayoutsNeverJumpBack()
    {
        var (window, list, viewer, controller, items) = CreateFeed();
        using (controller)
        {
            try
            {
                AssertVirtualized(list, items.Count);
                var point = viewer.TranslatePoint(new Point(50, 100), window)!.Value;
                window.MouseMove(point);
                for (var step = 0; step < 16; step++)
                {
                    var previous = viewer.Offset.Y;
                    window.MouseWheel(point, new Vector(0, -1));
                    Pump(window);
                    Assert.True(viewer.Offset.Y > previous, $"Wheel {step}: {previous} -> {viewer.Offset.Y}");
                    AssertStable(window, viewer, viewer.Offset.Y);
                }
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>没有鼠标事件伴随的惯性偏移不会被上一次偏移覆盖。</summary>
    [AvaloniaFact]
    public void OffsetChangesWithoutWheelInputRemainAuthoritative()
    {
        var (window, _, viewer, controller, _) = CreateFeed();
        using (controller)
        {
            try
            {
                foreach (var offset in new[] { 120d, 230, 315, 380, 420, 442, 451 })
                {
                    viewer.Offset = new(0, offset);
                    Pump(window);
                    AssertStable(window, viewer, offset);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>手势及递减惯性增量通过真实 ScrollContentPresenter 连续推进。</summary>
    [AvaloniaFact]
    public void ScrollGestureAndInertiaDeltasContinueDownward()
    {
        var (window, _, viewer, controller, _) = CreateFeed();
        using (controller)
        {
            try
            {
                var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>());
                var gestureId = ScrollGestureEventArgs.GetNextFreeId();
                var expected = 0d;
                foreach (var delta in new[] { 90d, 90, 75, 50, 30, 15, 5 })
                {
                    presenter.RaiseEvent(new ScrollGestureEventArgs(gestureId, new(0, delta)));
                    expected += delta;
                    Pump(window);
                    AssertStable(window, viewer, expected);
                }
                presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gestureId));
                Pump(window);
                AssertStable(window, viewer, expected);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>拖动真实滚动条滑块后，释放及后续布局保留用户选择的位置。</summary>
    [AvaloniaFact]
    public void ScrollbarThumbDragDoesNotRestoreOldOffset()
    {
        var (window, _, viewer, controller, _) = CreateFeed();
        using (controller)
        {
            try
            {
                viewer.AllowAutoHide = false;
                Pump(window);
                var bar = viewer.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(value => value.Orientation == Orientation.Vertical);
                var thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
                var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                var previous = viewer.Offset.Y;
                for (var step = 1; step <= 4; step++)
                {
                    window.MouseMove(start + new Vector(0, step * 12), RawInputModifiers.LeftMouseButton);
                    Pump(window);
                    Assert.True(viewer.Offset.Y > previous);
                    previous = viewer.Offset.Y;
                }
                window.MouseUp(start + new Vector(0, 48), MouseButton.Left);
                Pump(window);
                AssertStable(window, viewer, previous);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>头部新消息、已实现行高度变化及两者组合保留同一阅读锚点的视口位置。</summary>
    [AvaloniaTheory]
    [InlineData("insert")]
    [InlineData("height")]
    [InlineData("both")]
    [InlineData("tall")]
    public void CollectionAndContentChangesPreserveVisibleAnchor(string change)
    {
        var (window, list, viewer, controller, items) = CreateFeed();
        using (controller)
        {
            try
            {
                viewer.Offset = new(0, 650);
                Pump(window);
                var anchor = Assert.IsAssignableFrom<Control>(viewer.CurrentAnchor);
                var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
                var before = anchor.TranslatePoint(default, viewer)!.Value.Y;
                var preceding = items[items.IndexOf(item) - 1];
                Assert.NotNull(list.ContainerFromItem(preceding));
                if (change is "insert" or "both")
                {
                    for (var index = 0; index < 4; index++)
                    {
                        items.Insert(0, CreateItem($"new message {index}"));
                    }
                }
                if (change is "height" or "both" or "tall")
                {
                    preceding.Update(preceding.Record with
                    {
                        Content = string.Concat(Enumerable.Repeat("translated message with a taller wrapped body ",
                            change == "tall" ? 60 : 12))
                    }, new());
                }
                Pump(window);
                var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
                Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                AssertStable(window, viewer, viewer.Offset.Y);
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>默认无缓冲虚拟化中，批量插入使旧容器回收后仍定位同一消息。</summary>
    [AvaloniaTheory]
    [InlineData(4)]
    [InlineData(50)]
    [InlineData(500)]
    public void DefaultVirtualizationPreservesAnchorAcrossPrependBatches(int count)
    {
        var (window, list, viewer, controller, items) = CreateFeed(0);
        using (controller)
        {
            try
            {
                viewer.Offset = new(0, 650);
                Pump(window);
                var anchor = Assert.IsAssignableFrom<Control>(viewer.CurrentAnchor);
                var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
                var before = anchor.TranslatePoint(default, viewer)!.Value.Y;
                for (var index = 0; index < count; index++)
                {
                    items.Insert(0, CreateItem($"batch {index}"));
                }
                Pump(window);
                var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
                Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                AssertStable(window, viewer, viewer.Offset.Y);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>回到底部恢复新消息跟随，用户后续滚动取消已排队的跟随。</summary>
    [AvaloniaFact]
    public void ReturnToLatestFollowsNewMessagesAndYieldsToNewInput()
    {
        var (window, _, viewer, controller, items) = CreateFeed();
        using (controller)
        {
            try
            {
                var states = new List<bool>();
                controller.FollowingLatestChanged += states.Add;
                viewer.Offset = new(0, 300);
                Pump(window);
                controller.ScrollToLatest();
                Pump(window);
                AssertAtLatest(viewer);
                Assert.True(controller.IsFollowingLatest);
                items.Add(CreateItem("arrived while following"));
                Pump(window);
                AssertAtLatest(viewer);
                items.Add(CreateItem("queued before user scroll"));
                window.UpdateLayout();
                viewer.Offset = new(0, 300);
                Pump(window);
                AssertStable(window, viewer, 300);
                Assert.False(controller.IsFollowingLatest);
                viewer.ScrollToEnd();
                Pump(window);
                items.Add(CreateItem("follow after manually scrolling back to bottom"));
                Pump(window);
                AssertAtLatest(viewer);
                Assert.True(controller.IsFollowingLatest);
                Assert.Equal(new[] { true, false, true }, states);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>解绑、重新绑定及释放只通知状态变更，旧集合不能触发延迟回顶。</summary>
    [AvaloniaFact]
    public void DetachAndDisposeCancelQueuedFollowAndPublishStateChangesOnce()
    {
        var (window, _, viewer, controller, items) = CreateFeed(startAtLatest: true);
        using (controller)
        {
            try
            {
                var states = new List<bool>();
                controller.FollowingLatestChanged += states.Add;
                items.Insert(0, CreateItem("queued"));
                controller.Attach(null);
                controller.Attach(null);
                Pump(window);
                viewer.Offset = new(0, 240);
                Pump(window);
                AssertStable(window, viewer, 240);
                Assert.False(controller.IsFollowingLatest);
                controller.Attach(items);
                Pump(window);
                AssertAtLatest(viewer);
                controller.ScrollToLatest();
                controller.Dispose();
                viewer.Offset = new(0, 350);
                Pump(window);
                AssertStable(window, viewer, 350);
                Assert.False(controller.IsFollowingLatest);
                Assert.Equal(new[] { false, true, false }, states);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static (Window Window, ListBox List, ScrollViewer Viewer, DanmuFeedScrollController Controller,
        ObservableCollection<MessageItemViewModel> Items) CreateFeed(double cacheLength = 1, bool startAtLatest = false)
    {
        var items = new ObservableCollection<MessageItemViewModel>(
            Enumerable.Range(0, 200).Select(index => CreateItem($"message {index}")));
        var list = new ListBox
        {
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = cacheLength }),
            ItemTemplate = new FuncDataTemplate<MessageItemViewModel>((_, _) =>
            {
                var text = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(MessageItemViewModel.Detail)));
                return new Border { Padding = new(8), MinHeight = 34, Child = text };
            })
        };
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
        var window = new Window { Width = 380, Height = 300, Content = list };
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            var controller = new DanmuFeedScrollController(viewer, list);
            controller.Attach(items);
            Pump(window);
            if (!startAtLatest)
            {
                viewer.Offset = default;
                Pump(window);
            }
            return (window, list, viewer, controller, items);
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private static MessageItemViewModel CreateItem(string content)
    {
        return new(new MessageRecord
        {
            RoomTabId = "scroll-tests",
            RoomId = "123",
            Kind = MessageKind.Danmu,
            UserName = "viewer",
            Content = content
        }, new PanelSettings(), pinyin);
    }

    private static void AssertVirtualized(ListBox list, int itemCount)
    {
        Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, itemCount - 1);
    }

    private static void AssertAtLatest(ScrollViewer viewer)
    {
        Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
    }

    private static void AssertStable(Window window, ScrollViewer viewer, double offset)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            window.InvalidateMeasure();
            Pump(window);
            Assert.InRange(Math.Abs(viewer.Offset.Y - offset), 0, 0.1);
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
