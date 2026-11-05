using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证布局无法收敛时底部跟随仍保持有界，并让出输入队列。</summary>
public sealed class FeedFollowSchedulingTests
{
    private const int MAX_CORRECTIONS_BEFORE_FUSE = 16;
    private static readonly IPinyinService pinyin = new PinyinService();

    /// <summary>持续的延迟偏移纠正不能让跟随任务无限抢占输入，测试会在十六次后自行熔断。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonConvergingLayoutCorrectionsYieldToInputAndDoNotRetryIndefinitely(bool changeExtent)
    {
        var (window, list, viewer, controller, items) = CreateFeed();
        using (controller)
        {
            var corrections = 0;
            var correctionQueued = false;
            var inputExecuted = false;
            var correctionsAtInput = 0;
            var fuseTripped = false;
            var stopped = false;
            void CorrectAfterLayout(object? sender, EventArgs args)
            {
                if (stopped || correctionQueued || viewer.ScrollBarMaximum.Y - viewer.Offset.Y > 1)
                {
                    return;
                }
                correctionQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    correctionQueued = false;
                    if (stopped)
                    {
                        return;
                    }
                    corrections++;
                    if (corrections == MAX_CORRECTIONS_BEFORE_FUSE)
                    {
                        fuseTripped = true;
                        stopped = true;
                        controller.Dispose();
                        return;
                    }
                    if (corrections == 1)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            correctionsAtInput = corrections;
                            inputExecuted = true;
                        }, DispatcherPriority.Input);
                    }
                    viewer.Offset = new(0, Math.Max(0, viewer.ScrollBarMaximum.Y - 8));
                    if (changeExtent && list.ContainerFromItem(items[^1]) is { } last)
                    {
                        last.GetVisualDescendants().OfType<TextBlock>().Single().Height = 32 + corrections * 3;
                        window.UpdateLayout();
                    }
                }, DispatcherPriority.Loaded);
            }
            viewer.LayoutUpdated += CorrectAfterLayout;
            try
            {
                items.Add(CreateItem("new content with a deferred presenter correction"));
                Pump(window);

                Assert.True(corrections > 0, "The simulated presenter did not correct the follow position.");
                Assert.True(inputExecuted, "The input probe was starved by follow work.");
                Assert.True(correctionsAtInput < MAX_CORRECTIONS_BEFORE_FUSE,
                    $"Input ran only after {correctionsAtInput} corrections and the test fuse disposed the controller.");
                Assert.False(fuseTripped, "The follow scheduler kept retrying without new content until the test fuse fired.");
                Assert.InRange(corrections, 1, MAX_CORRECTIONS_BEFORE_FUSE - 1);
                Assert.True(controller.IsFollowingLatest, "Layout corrections were mistaken for a request to read history.");

                stopped = true;
                viewer.LayoutUpdated -= CorrectAfterLayout;
                items.Add(CreateItem("new content resumes following after the bounded transaction"));
                Pump(window);
                Assert.True(controller.IsFollowingLatest);
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            }
            finally
            {
                stopped = true;
                viewer.LayoutUpdated -= CorrectAfterLayout;
                window.Close();
            }
        }
    }

    /// <summary>外部布局通知合并定位，并在历史阅读时保留可见消息的相对位置。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalLayoutChangesCoalesceAndRespectReadingPosition(bool readHistory)
    {
        var (window, list, viewer, controller, _) = CreateFeed();
        using (controller)
        {
            try
            {
                if (readHistory)
                {
                    viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
                    Pump(window);
                    viewer.Offset = new(0, 650);
                    Pump(window);
                }
                var anchor = Assert.IsAssignableFrom<Control>(viewer.CurrentAnchor);
                var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor));
                var before = anchor.TranslatePoint(default, viewer)!.Value.Y;
                var requests = 0;
                list.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++);
                controller.NotifyLayoutChanged();
                controller.NotifyLayoutChanged();
                foreach (var text in list.GetVisualDescendants().OfType<TextBlock>())
                {
                    text.Height = 64;
                }
                Pump(window);

                if (readHistory)
                {
                    var retained = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
                    Assert.InRange(Math.Abs(retained.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                    Assert.False(controller.IsFollowingLatest);
                }
                else
                {
                    Assert.InRange(requests, 0, 2);
                    Assert.True(controller.IsFollowingLatest);
                    Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>结算阶段已经排队的真实新内容不能被误标为处理完成。</summary>
    [AvaloniaFact]
    public void NewContentQueuedDuringSettlementReceivesItsOwnFollowTransaction()
    {
        var (window, list, viewer, controller, items) = CreateFeed();
        using (controller)
        {
            try
            {
                var requests = 0;
                var appended = false;
                list.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++);
                items.Add(CreateItem("starts the follow transaction"));
                Dispatcher.UIThread.Post(() =>
                {
                    viewer.Offset = new(0, viewer.Offset.Y - 34);
                    Dispatcher.UIThread.Post(() =>
                    {
                        appended = true;
                        items.Add(CreateItem("arrived before the input settlement completes"));
                    }, DispatcherPriority.Loaded);
                }, DispatcherPriority.Loaded);
                Pump(window);

                Assert.True(appended);
                Assert.True(controller.IsFollowingLatest);
                Assert.NotNull(list.ContainerFromItem(items[^1]));
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                Assert.InRange(requests, 0, 4);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>视口缩放产生新的定位事务，不沿用已结算视口的底部偏移。</summary>
    [AvaloniaFact]
    public void ViewportChangesStartNewBoundedFollowTransactions()
    {
        var (window, _, viewer, controller, _) = CreateFeed();
        using (controller)
        {
            try
            {
                foreach (var height in new[] { 180d, 420, 260 })
                {
                    window.Height = height;
                    Pump(window);
                    Assert.True(controller.IsFollowingLatest);
                    Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>非自动跟随面板在首次排队前收到消息，也能完成队列并保持用户滚动权威。</summary>
    [AvaloniaFact]
    public void NonFollowingFeedClearsItsInitialPendingWorkWhenMessagesArrive()
    {
        var (window, list, viewer, controller, _) = CreateFeed(followNewMessages: false);
        using (controller)
        {
            try
            {
                var items = new ObservableCollection<MessageItemViewModel>();
                list.ItemsSource = items;
                controller.Attach(items);
                foreach (var index in Enumerable.Range(0, 100))
                {
                    items.Add(CreateItem($"arrived before first layout {index}"));
                }
                var inputExecuted = false;
                Dispatcher.UIThread.Post(() => inputExecuted = true, DispatcherPriority.Input);
                Pump(window);
                Assert.True(inputExecuted);

                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
                Pump(window);
                viewer.Offset = new(0, 120);
                Pump(window);
                Assert.InRange(Math.Abs(viewer.Offset.Y - 120), 0, 0.1);
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>同一行的一批属性变化只需要一次定位工作，不为每个通知重复滚到底部。</summary>
    [AvaloniaFact]
    public void MultiplePropertiesChangedBeforeDispatchCoalesceIntoOneFollow()
    {
        var (window, list, _, controller, items) = CreateFeed();
        using (controller)
        {
            try
            {
                var requests = 0;
                list.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++);
                var item = items[^1];
                var notifications = 0;
                item.PropertyChanged += (_, _) => notifications++;

                item.Update(item.Record with
                {
                    UserName = "updated viewer",
                    Content = "updated content",
                    MedalName = "medal",
                    MedalLevel = 3,
                    IsPinned = true
                }, new PanelSettings());
                Pump(window);

                Assert.True(notifications > 1);
                Assert.InRange(requests, 0, 1);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>释放控制器会使已经排队的跟随失效，后续布局和输入不能重新激活它。</summary>
    [AvaloniaFact]
    public void DisposeInvalidatesQueuedFollowWork()
    {
        var (window, list, viewer, controller, items) = CreateFeed();
        using (controller)
        {
            try
            {
                var requests = 0;
                var inputExecuted = false;
                list.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => requests++);
                items.Add(CreateItem("queued before disposal"));
                controller.Dispose();
                viewer.Offset = new(0, 100);
                window.UpdateLayout();
                var expectedOffset = viewer.Offset.Y;
                Dispatcher.UIThread.Post(() => inputExecuted = true, DispatcherPriority.Input);

                Pump(window);

                Assert.True(inputExecuted);
                Assert.Equal(0, requests);
                Assert.InRange(Math.Abs(viewer.Offset.Y - expectedOffset), 0, 0.1);
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>消息移组前的属性通知和中途调度不能将已准备的邻居锚点改回待移动行。</summary>
    [AvaloniaFact]
    public void PreparedNeighborAnchorSurvivesPropertyNotificationsBeforeTheCollectionMove()
    {
        var (window, list, viewer, controller, items) = CreateFeed(followNewMessages: false);
        using (controller)
        {
            try
            {
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
                Pump(window);
                viewer.Offset = new(0, viewer.ScrollBarMaximum.Y / 3);
                Pump(window);
                var clicked = list.GetRealizedContainers()
                    .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                        position.Y >= 0 && position.Y + container.Bounds.Height <= viewer.Viewport.Height)
                    .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
                var moving = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(clicked));
                viewer.Offset = new(0, viewer.Offset.Y + clicked.TranslatePoint(default, viewer)!.Value.Y);
                Pump(window);
                var movingIndex = items.IndexOf(moving);
                var retained = list.GetRealizedContainers().Where(container =>
                        list.ItemFromContainer(container) is MessageItemViewModel actual && !ReferenceEquals(actual, moving) &&
                        container.TranslatePoint(default, viewer) is { } position && position.Y < viewer.Viewport.Height &&
                        position.Y + container.Bounds.Height > 0)
                    .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
                var neighbor = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(retained));
                var before = retained.TranslatePoint(default, viewer)!.Value.Y;
                controller.PrepareForItemMove(moving);

                moving.Update(moving.Record with { Content = "property updated before the collection move" }, new());
                controller.NotifyLayoutChanged();
                Pump(window);
                Pump(window);
                items.Move(movingIndex, items.Count - 1);
                Pump(window);

                var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(neighbor));
                Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>消息无需移组时，结束操作会释放准备状态，后续属性及行高变化仍纠正阅读锚点。</summary>
    [AvaloniaFact]
    public void CompletingAnUnmovedItemRestoresAnchorCorrectionsForLaterPropertyChanges()
    {
        var (window, list, viewer, controller, items) = CreateFeed(followNewMessages: false);
        using (controller)
        {
            try
            {
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
                Pump(window);
                viewer.Offset = new(0, viewer.ScrollBarMaximum.Y / 3);
                Pump(window);
                var clicked = list.GetRealizedContainers()
                    .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                        position.Y >= 0 && position.Y + container.Bounds.Height <= viewer.Viewport.Height)
                    .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
                var moving = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(clicked));
                viewer.Offset = new(0, viewer.Offset.Y + clicked.TranslatePoint(default, viewer)!.Value.Y);
                Pump(window);
                var retained = list.GetRealizedContainers().Where(container =>
                        list.ItemFromContainer(container) is MessageItemViewModel actual && !ReferenceEquals(actual, moving) &&
                        container.TranslatePoint(default, viewer) is { } position && position.Y < viewer.Viewport.Height &&
                        position.Y + container.Bounds.Height > 0)
                    .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
                var neighbor = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(retained));
                var before = retained.TranslatePoint(default, viewer)!.Value.Y;
                controller.PrepareForItemMove(moving);
                moving.Update(moving.Record with { Content = "operation keeps the same list position" }, new());
                Pump(window);

                controller.CompleteItemMove();
                var unchangedContainer = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(moving));
                Assert.Single(unchangedContainer.GetVisualDescendants().OfType<TextBlock>()).Height = 72;
                moving.Update(moving.Record with { Content = "later content and layout update" }, new());
                Pump(window);

                var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(neighbor));
                Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static (Window Window, ListBox List, ScrollViewer Viewer, DanmuFeedScrollController Controller,
        ObservableCollection<MessageItemViewModel> Items) CreateFeed(bool followNewMessages = true)
    {
        var items = new ObservableCollection<MessageItemViewModel>(
            Enumerable.Range(0, 200).Select(index => CreateItem($"message {index}")));
        var list = new ListBox
        {
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<MessageItemViewModel>((_, _) =>
            {
                var text = new TextBlock { Height = 32, TextWrapping = TextWrapping.NoWrap };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(MessageItemViewModel.Detail)));
                return text;
            })
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var window = new Window { Width = 380, Height = 300, Content = list };
        DanmuFeedScrollController? controller = null;
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            controller = new(viewer, list, followNewMessages);
            controller.Attach(items);
            Pump(window);
            Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            Assert.InRange(list.GetRealizedContainers().Count(), 1, items.Count - 1);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            return (window, list, viewer, controller, items);
        }
        catch
        {
            controller?.Dispose();
            window.Close();
            throw;
        }
    }

    private static MessageItemViewModel CreateItem(string content)
    {
        return new(new MessageRecord
        {
            RoomTabId = "follow-scheduling",
            Kind = MessageKind.Danmu,
            UserName = "viewer",
            Content = content
        }, new PanelSettings(), pinyin);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
