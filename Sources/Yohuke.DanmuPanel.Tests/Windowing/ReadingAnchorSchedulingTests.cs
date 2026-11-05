using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证无新消息时的布局收敛不会将历史阅读晋升为追底或饿死输入。</summary>
public sealed class ReadingAnchorSchedulingTests(ITestOutputHelper output)
{
    private const string ROW_HEIGHT_RESOURCE = "ReadingAnchorTestRowHeight";
    private const int MAX_LAYOUT_FEEDBACK_BEFORE_FUSE = 16;

    /// <summary>列表临时变短触发原生偏移钳制后，恢复布局仍保持阅读状态，不强制定位尾部。</summary>
    [AvaloniaFact]
    public void TemporaryExtentCollapseCannotPromoteReadingToFollowing()
    {
        var (window, list, viewer, controller) = CreateFeed(virtualizing: false);
        using (controller)
        {
            try
            {
                NavigateToHistory(window, viewer, 0.6);
                Assert.False(controller.IsFollowingLatest);
                var before = viewer.Offset.Y;
                var bringRequests = 0;
                list.AddHandler(Control.RequestBringIntoViewEvent, (_, _) => bringRequests++);

                list.Resources[ROW_HEIGHT_RESOURCE] = 1d;
                Pump(window);
                var temporaryOffset = viewer.Offset.Y;
                var temporaryMaximum = viewer.ScrollBarMaximum.Y;
                var followedAfterCollapse = controller.IsFollowingLatest;
                list.Resources[ROW_HEIGHT_RESOURCE] = 32d;
                Pump(window);

                output.WriteLine($"Reading before collapse: offset={before:F2}; temporary offset={temporaryOffset:F2}, " +
                    $"maximum={temporaryMaximum:F2}, following={followedAfterCollapse}; restored offset={viewer.Offset.Y:F2}, " +
                    $"maximum={viewer.ScrollBarMaximum.Y:F2}, following={controller.IsFollowingLatest}; bring requests={bringRequests}.");
                Assert.InRange(temporaryMaximum, 0, 1);
                Assert.False(followedAfterCollapse, "Native layout coercion is not a user request to follow new messages.");
                Assert.False(controller.IsFollowingLatest);
                Assert.Equal(0, bringRequests);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>历史阅读中连续的延迟行高修正必须让出输入；十六次反馈熔断保护测试自身。</summary>
    [AvaloniaFact]
    public void ReadingLayoutFeedbackYieldsToInputBeforeTheFuse()
    {
        var (window, list, viewer, controller) = CreateFeed(virtualizing: false);
        using (controller)
        {
            var feedbackCount = 0;
            var feedbackQueued = false;
            var inputExecuted = false;
            var feedbackAtInput = 0;
            var fuseTripped = false;
            var stopped = false;
            Control? anchor = null;
            var expectedY = 0d;
            void QueueDeferredLayoutFeedback(object? sender, EventArgs args)
            {
                if (stopped || feedbackQueued || anchor?.TranslatePoint(default, viewer) is not { } position ||
                    Math.Abs(position.Y - expectedY) > 0.1)
                {
                    return;
                }
                feedbackQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    feedbackQueued = false;
                    if (stopped)
                    {
                        return;
                    }
                    feedbackCount++;
                    if (feedbackCount == MAX_LAYOUT_FEEDBACK_BEFORE_FUSE)
                    {
                        fuseTripped = true;
                        stopped = true;
                        controller.Dispose();
                        return;
                    }
                    if (feedbackCount == 1)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            inputExecuted = true;
                            feedbackAtInput = feedbackCount;
                            stopped = true;
                        }, DispatcherPriority.Input);
                    }
                    list.Resources[ROW_HEIGHT_RESOURCE] = feedbackCount % 2 == 0 ? 32d : 36d;
                    controller.NotifyLayoutChanged();
                }, DispatcherPriority.Loaded);
            }
            try
            {
                NavigateToHistory(window, viewer, 0.45);
                Assert.False(controller.IsFollowingLatest);
                Assert.Null(viewer.CurrentAnchor);
                Assert.True(viewer.Offset.Y > viewer.Viewport.Height, "The feedback fixture must start within history.");
                anchor = list.GetRealizedContainers()
                    .Where(container => container.TranslatePoint(default, viewer) is { } position &&
                        position.Y + container.Bounds.Height > 0 && position.Y < viewer.Viewport.Height)
                    .OrderBy(container => container.TranslatePoint(default, viewer)!.Value.Y).First();
                viewer.Offset = new(0, viewer.Offset.Y + anchor.TranslatePoint(default, viewer)!.Value.Y);
                Pump(window);
                expectedY = anchor.TranslatePoint(default, viewer)!.Value.Y;
                viewer.LayoutUpdated += QueueDeferredLayoutFeedback;

                list.Resources[ROW_HEIGHT_RESOURCE] = 34d;
                controller.NotifyLayoutChanged();
                Pump(window);

                output.WriteLine($"Reading layout feedback={feedbackCount}; input ran after={feedbackAtInput}; " +
                    $"fuse={fuseTripped}; following={controller.IsFollowingLatest}; offset={viewer.Offset.Y:F2}.");
                Assert.True(feedbackCount > 0);
                Assert.True(inputExecuted, "Reading anchor correction must not starve pending input.");
                Assert.False(fuseTripped, "The reading correction loop reached the test fuse before allowing input.");
                Assert.InRange(feedbackAtInput, 1, MAX_LAYOUT_FEEDBACK_BEFORE_FUSE - 1);
                Assert.False(controller.IsFollowingLatest);
            }
            finally
            {
                stopped = true;
                viewer.LayoutUpdated -= QueueDeferredLayoutFeedback;
                window.Close();
            }
        }
    }

    /// <summary>批次中 Home 或 End 即使不产生偏移事件，也替换旧阅读目标，后续消息更新尊重新位置。</summary>
    [AvaloniaTheory]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    public void UserNavigationWithoutAnOffsetChangeReplacesTheFrozenBatchAnchor(Key key)
    {
        var (window, list, viewer, controller) = CreateFeed();
        using (controller)
        {
            var updating = false;
            try
            {
                NavigateToHistory(window, viewer, 0.6);
                Assert.False(controller.IsFollowingLatest);
                controller.BeginViewportUpdate();
                updating = true;
                viewer.Offset = key == Key.Home ? default : new(0, viewer.ScrollBarMaximum.Y);
                var offsetBeforeInput = viewer.Offset;
                var changesDuringInput = 0;
                void ObserveOffset(object? sender, AvaloniaPropertyChangedEventArgs args)
                {
                    if (args.Property == ScrollViewer.OffsetProperty)
                    {
                        changesDuringInput++;
                    }
                }
                viewer.PropertyChanged += ObserveOffset;
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                viewer.PropertyChanged -= ObserveOffset;
                Assert.Equal(offsetBeforeInput, viewer.Offset);
                Assert.Equal(0, changesDuringInput);
                controller.NotifyLayoutChanged();
                Pump(window);

                var items = Assert.IsType<ObservableCollection<MessageItemViewModel>>(list.ItemsSource);
                if (key == Key.Home)
                {
                    Assert.False(controller.IsFollowingLatest);
                    Assert.InRange(Math.Abs(viewer.Offset.Y), 0, 1);
                    var retained = items[0];
                    var container = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(retained));
                    var before = container.TranslatePoint(default, viewer)!.Value.Y;
                    items.Insert(0, new(new() { RoomTabId = "reading-anchor", UserName = "viewer", Content = "older history" },
                        new PanelSettings(), new PinyinService()));
                    Pump(window);
                    var after = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(retained));
                    Assert.InRange(Math.Abs(after.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
                    Assert.False(controller.IsFollowingLatest);
                }
                else
                {
                    Assert.True(controller.IsFollowingLatest);
                    items.Add(new(new() { RoomTabId = "reading-anchor", UserName = "viewer", Content = "latest message" },
                        new PanelSettings(), new PinyinService()));
                    Pump(window);
                    Assert.True(controller.IsFollowingLatest);
                    Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                    Assert.NotNull(list.ContainerFromItem(items[^1]));
                }
            }
            finally
            {
                if (updating)
                {
                    controller.EndViewportUpdate();
                }
                window.Close();
            }
        }
    }

    /// <summary>窗口宽度改变末行换行高度后，以新视口开始跟随结算并保持底部位置。</summary>
    [AvaloniaFact]
    public void FollowingLatestSurvivesWidthChangesThatRewrapTheLastMessage()
    {
        var (window, list, viewer, controller) = CreateFeed(wrapping: true);
        using (controller)
        {
            try
            {
                var lastItem = list.Items[^1]!;
                var initial = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(lastItem));
                var initialHeight = initial.Bounds.Height;
                Assert.True(controller.IsFollowingLatest);
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);

                window.Width = 240;
                Pump(window);
                Pump(window);
                var narrow = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(lastItem));
                var narrowHeight = narrow.Bounds.Height;
                Assert.True(narrowHeight > initialHeight, "The narrower viewport must increase the final message height.");
                Assert.True(controller.IsFollowingLatest);
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);

                window.Width = 520;
                Pump(window);
                Pump(window);
                var wide = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(lastItem));
                Assert.True(wide.Bounds.Height < narrowHeight, "The wider viewport must reduce the final message height.");
                for (var frame = 0; frame < 4; frame++)
                {
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

    private static void NavigateToHistory(Window window, ScrollViewer viewer, double fraction)
    {
        viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home });
        Pump(window);
        viewer.Offset = new(0, viewer.ScrollBarMaximum.Y * fraction);
        Pump(window);
    }

    private static (Window Window, ListBox List, ScrollViewer Viewer, DanmuFeedScrollController Controller) CreateFeed(
        bool virtualizing = true, bool wrapping = false)
    {
        var pinyin = new PinyinService();
        var items = new ObservableCollection<MessageItemViewModel>(Enumerable.Range(0, 200).Select(index =>
            new MessageItemViewModel(new MessageRecord
            {
                RoomTabId = "reading-anchor", Kind = MessageKind.Danmu, UserName = "viewer",
                Content = wrapping ? $"message {index} a long message with enough words to wrap across several visible lines while the window changes width" : $"message {index}"
            }, new PanelSettings(), pinyin)));
        var list = new ListBox
        {
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => virtualizing ? new VirtualizingStackPanel() : new StackPanel()),
            ItemTemplate = new FuncDataTemplate<MessageItemViewModel>((_, _) =>
            {
                var text = new TextBlock { TextWrapping = wrapping ? TextWrapping.Wrap : TextWrapping.NoWrap };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(MessageItemViewModel.Detail)));
                if (!wrapping)
                {
                    text.Bind(Layoutable.HeightProperty, new DynamicResourceExtension(ROW_HEIGHT_RESOURCE));
                }
                return text;
            })
        };
        list.Resources[ROW_HEIGHT_RESOURCE] = 32d;
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, 0d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0))
            }
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var window = new Window { Width = 380, Height = 300, Content = list };
        DanmuFeedScrollController? controller = null;
        try
        {
            window.Show();
            Pump(window);
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            controller = new(viewer, list);
            controller.Attach(items);
            Pump(window);
            return (window, list, viewer, controller);
        }
        catch
        {
            controller?.Dispose();
            window.Close();
            throw;
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
