using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证未监听时，历史分页和异步注音不会造成阅读位置漂移或持续布局排队。</summary>
public sealed class HistoryViewportStabilityTests(ITestOutputHelper output)
{
    private const int HISTORY_COUNT = 2200;
    private const int MAX_LAYOUT_PASSES = 12000;
    private const int MAX_OFFSET_CHANGES = 2048;
    private const int MAX_BRING_REQUESTS = 512;
    private const int MAX_WAIT_FRAMES = 600;
    private const int QUIET_FRAMES = 24;
    private const string EMOTICON_URL = "https://example.invalid/history-smile.png";

    /// <summary>连续加载三页并异步补齐注音后保持同一可见行位置，置顶与取消置顶后仍能收敛。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IdleHistoryAndPinyinKeepViewportStable(bool compact, bool returnToLatestBeforePin)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.Translation.Enabled = false;
        settings.ShowUserPinyin = true;
        settings.ShowDanmuPinyin = true;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = false;
        settings.AutoScrollLongMessages = false;
        settings.DanmuRegexFilter.Enabled = false;
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration, pinyin);
        await SeedHistoryAsync(runtime, directory);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        Assert.False(runtime.IsListening(room));
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = compact ? 380 : 420, Height = 520, Content = view };
        ScrollViewer? viewer = null;
        ListBox? list = null;
        var layoutPasses = 0;
        var offsetChanges = 0;
        var bringRequests = 0;
        var inputProbes = 0;
        var fuseTripped = false;
        var fuseReason = "";

        void TripFuse(string reason)
        {
            if (fuseTripped)
            {
                return;
            }
            fuseTripped = true;
            fuseReason = reason;
            view.Model.Dispose();
            window.Close();
        }

        void OnLayoutUpdated(object? sender, EventArgs args)
        {
            if (++layoutPasses >= MAX_LAYOUT_PASSES)
            {
                TripFuse("layout passes");
            }
        }

        void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == ScrollViewer.OffsetProperty && ++offsetChanges >= MAX_OFFSET_CHANGES)
            {
                TripFuse("offset changes");
            }
        }

        void OnBringIntoView(object? sender, RequestBringIntoViewEventArgs args)
        {
            if (++bringRequests >= MAX_BRING_REQUESTS)
            {
                TripFuse("bring-into-view requests");
            }
        }

        async Task FrameAsync()
        {
            await Task.Delay(1);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => inputProbes++, DispatcherPriority.Input);
            Assert.False(fuseTripped,
                $"Non-converging {fuseReason}: layout={layoutPasses}, offsets={offsetChanges}, bring={bringRequests}.");
        }

        async Task CompleteAsync(Task task, string stage, MessageItemViewModel? anchor = null, double anchorY = 0)
        {
            var elapsed = Stopwatch.StartNew();
            var frames = 0;
            while (!task.IsCompleted && frames++ < MAX_WAIT_FRAMES && elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                await FrameAsync();
                if (anchor is not null)
                {
                    Assert.Contains(anchor, view.Model.ScrollingItems);
                }
            }
            Assert.True(task.IsCompleted,
                $"{stage} did not complete: frames={frames}, layout={layoutPasses}, offsets={offsetChanges}, bring={bringRequests}.");
            await task;
            await FrameAsync();
            if (anchor is not null)
            {
                for (var settle = 0; settle < 4; settle++)
                {
                    await FrameAsync();
                }
                AssertReadingPosition(list!, viewer!, anchor, anchorY, stage);
            }
            output.WriteLine($"{stage}: {elapsed.ElapsedMilliseconds} ms, {frames} frames, offset={viewer?.Offset.Y:F2}, " +
                $"maximum={viewer?.ScrollBarMaximum.Y:F2}, layout={layoutPasses}, offsets={offsetChanges}, bring={bringRequests}, input={inputProbes}.");
        }

        async Task AssertQuietAsync(string stage, MessageItemViewModel? anchor, double anchorY, bool atLatest)
        {
            for (var frame = 0; frame < 12; frame++)
            {
                await FrameAsync();
            }
            var beforeLayout = layoutPasses;
            var beforeBring = bringRequests;
            var beforeInput = inputProbes;
            var offset = viewer!.Offset.Y;
            var minimumOffset = offset;
            var maximumOffset = offset;
            for (var frame = 0; frame < QUIET_FRAMES; frame++)
            {
                await FrameAsync();
                minimumOffset = Math.Min(minimumOffset, viewer.Offset.Y);
                maximumOffset = Math.Max(maximumOffset, viewer.Offset.Y);
                if (atLatest)
                {
                    Assert.False(view.Model.IsReadingHistory);
                    Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                }
                else
                {
                    Assert.True(view.Model.IsReadingHistory);
                    AssertReadingPosition(list!, viewer, anchor!, anchorY, $"{stage}, quiet frame {frame}");
                }
            }
            output.WriteLine($"{stage} quiet: offset range={maximumOffset - minimumOffset:F3}, " +
                $"layout={layoutPasses - beforeLayout}, bring={bringRequests - beforeBring}, input={inputProbes - beforeInput}.");
            Assert.InRange(maximumOffset - minimumOffset, 0, 1);
            Assert.Equal(beforeBring, bringRequests);
            Assert.InRange(layoutPasses - beforeLayout, 0, QUIET_FRAMES * 2);
            Assert.Equal(QUIET_FRAMES, inputProbes - beforeInput);
        }

        window.LayoutUpdated += OnLayoutUpdated;
        try
        {
            window.Show();
            await CompleteAsync(view.Model.SelectRoomAsync(room), "initial page");
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            list = FindList(view, "ScrollingMessages");
            viewer = FindViewer(list);
            viewer.PropertyChanged += OnScrollPropertyChanged;
            list.AddHandler(Control.RequestBringIntoViewEvent, OnBringIntoView, RoutingStrategies.Tunnel, true);
            for (var frame = 0; frame < 6; frame++)
            {
                await FrameAsync();
            }
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                candidate => ReferenceEquals(candidate.FindAncestorOfType<ScrollViewer>(), viewer));
            var targetOffset = viewer.ScrollBarMaximum.Y * 0.42;
            var gestureId = ScrollGestureEventArgs.GetNextFreeId();
            presenter.RaiseEvent(new ScrollGestureEventArgs(gestureId, new(0, targetOffset - viewer.Offset.Y)));
            presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gestureId));
            for (var frame = 0; frame < 6; frame++)
            {
                await FrameAsync();
            }
            Assert.True(view.Model.IsReadingHistory);
            Assert.True(viewer.Offset.Y > viewer.Viewport.Height);
            Assert.True(viewer.ScrollBarMaximum.Y - viewer.Offset.Y > viewer.Viewport.Height);
            var partial = VisibleMessages(list, viewer).OrderBy(value => value.Y).First();
            if (partial.Y >= -1)
            {
                var partialGesture = ScrollGestureEventArgs.GetNextFreeId();
                presenter.RaiseEvent(new ScrollGestureEventArgs(partialGesture, new(0, 13)));
                presenter.RaiseEvent(new ScrollGestureEndedEventArgs(partialGesture));
                for (var frame = 0; frame < 4; frame++)
                {
                    await FrameAsync();
                }
            }
            var anchor = VisibleMessages(list, viewer).OrderBy(value => value.Y).First();
            Assert.True(anchor.Y < -1, "History reading must retain a partially visible top message.");
            for (var page = 0; page < 3; page++)
            {
                await CompleteAsync(view.Model.LoadMoreAsync(), $"history page {page + 1}", anchor.Item, anchor.Y);
                Assert.Equal((page + 2) * 100, view.Model.ScrollingItems.Count);
                if (page == 0)
                {
                    Assert.Contains(view.Model.Items, item => item.IsPresentationPending);
                    pinyin.Release.TrySetResult();
                }
            }
            await CompleteAsync(view.Model.WaitForPresentationAsync(), "presentation catch-up", anchor.Item, anchor.Y);
            Assert.All(view.Model.Items, item => Assert.False(item.IsPresentationPending));
            await AssertQuietAsync("history complete", anchor.Item, anchor.Y, false);

            if (returnToLatestBeforePin)
            {
                var actions = Assert.IsType<StackPanel>(view.HeaderActionsTemplate.Build(view));
                var latest = actions.Children.OfType<Button>().Single(button => button.Name == "BackToLatestButton");
                await CompleteAsync(Assert.IsAssignableFrom<IAsyncRelayCommand>(latest.Command).ExecuteAsync(null), "back to latest");
                await AssertQuietAsync("latest before pin", null, 0, true);
            }
            var visible = VisibleMessages(list, viewer).OrderBy(value => value.Y).ToArray();
            Assert.True(visible.Length >= 2, "The viewport needs two real messages to verify pinning preserves a neighbor.");
            var neighbor = visible[0];
            var target = visible[1].Item;
            var row = Assert.Single(list.ContainerFromItem(target)!.GetVisualDescendants()
                .OfType<Button>(), button => ReferenceEquals(button.DataContext, target));
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await CompleteAsync(WaitForPinAsync(target, true), "pin commit");
            await CompleteAsync(view.Model.WaitForPresentationAsync(), "pin presentation");
            await AssertQuietAsync("after pin", neighbor.Item, neighbor.Y, returnToLatestBeforePin);

            var pinned = FindList(view, "PinnedMessages");
            var pinnedRow = Assert.Single(pinned.GetRealizedContainers().SelectMany(container => container.GetVisualDescendants())
                .OfType<Button>(), button => ReferenceEquals(button.DataContext, target));
            pinnedRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await CompleteAsync(WaitForPinAsync(target, false), "unpin commit");
            await CompleteAsync(view.Model.WaitForPresentationAsync(), "unpin presentation");
            await AssertQuietAsync("after unpin", neighbor.Item, neighbor.Y, returnToLatestBeforePin);
            Assert.False(runtime.IsListening(room));
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            window.LayoutUpdated -= OnLayoutUpdated;
            if (viewer is not null)
            {
                viewer.PropertyChanged -= OnScrollPropertyChanged;
            }
            list?.RemoveHandler(Control.RequestBringIntoViewEvent, OnBringIntoView);
            view.Model.Dispose();
            window.Close();
        }
    }

    private static async Task SeedHistoryAsync(PanelRuntime runtime, MessageStoreTestDirectory directory)
    {
        var imageDirectory = Path.Combine(directory.Path, "Cache", "Emoticons");
        Directory.CreateDirectory(imageDirectory);
        var imageName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EMOTICON_URL))).ToLowerInvariant() + ".img";
        await File.WriteAllBytesAsync(Path.Combine(imageDirectory, imageName), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGMI6LnzH4QZYAwAXRQK3dFXl4QAAAAASUVORK5CYII="));
        var names = new[] { "小明", "宇神", "重庆观众", "Long viewer 名字", "おやすみ" };
        var messages = Enumerable.Range(0, HISTORY_COUNT).Select(index => new MessageRecord
        {
            Id = $"history-{index}", RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu, IsTest = true,
            UserName = names[index % names.Length], MedalName = "粉丝牌", MedalLevel = index % 30 + 1,
            Content = (index % 5) switch
            {
                0 => $"晚安 {index}",
                1 => string.Concat(Enumerable.Repeat($"今晚一起听直播吧{index}，", 6)),
                2 => $"你好👨‍👩‍👧‍👦❤️一起旅行，重庆银行的朋友们晚上好 {index}",
                3 => $"[smile] 你好，连续的协议表情和文字 [smile] {index}",
                _ => $"mixed ABC 123 中文 {index}\n下一行再见"
            },
            Emoticons = index % 5 == 3 ? [new() { Text = "[smile]", Url = EMOTICON_URL }] : [],
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        });
        foreach (var batch in messages.Chunk(500))
        {
            await runtime.Messages.AppendTestMessagesAsync(batch, runtime.Configuration.Settings.Reading);
        }
    }

    private static (MessageItemViewModel Item, double Y)[] VisibleMessages(ListBox list, ScrollViewer viewer)
    {
        return list.GetRealizedContainers().Where(container => container.IsEffectivelyVisible &&
                list.ItemFromContainer(container) is MessageItemViewModel &&
                container.TranslatePoint(default, viewer) is { } position && position.Y < viewer.Viewport.Height &&
                position.Y + container.Bounds.Height > 0)
            .Select(container => ((MessageItemViewModel)list.ItemFromContainer(container)!,
                container.TranslatePoint(default, viewer)!.Value.Y)).ToArray();
    }

    private static void AssertReadingPosition(ListBox list, ScrollViewer viewer, MessageItemViewModel item,
        double expectedY, string stage)
    {
        var container = list.ContainerFromItem(item);
        Assert.True(container is not null, $"{stage}: visible anchor {item.Record.Id} disappeared at offset {viewer.Offset.Y:F2}.");
        var actual = container.TranslatePoint(default, viewer)!.Value.Y;
        Assert.True(Math.Abs(actual - expectedY) <= 1,
            $"{stage}: anchor {item.Record.Id} moved from {expectedY:F3} to {actual:F3}; " +
            $"offset={viewer.Offset.Y:F3}, maximum={viewer.ScrollBarMaximum.Y:F3}.");
    }

    private static async Task WaitForPinAsync(MessageItemViewModel item, bool pinned)
    {
        for (var attempt = 0; attempt < 200 && item.Record.IsPinned != pinned; attempt++)
        {
            await Task.Delay(5);
        }
        Assert.Equal(pinned, item.Record.IsPinned);
    }

    private static ListBox FindList(MessageFeedView view, string name)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == name);
    }

    private static ScrollViewer FindViewer(ListBox list)
    {
        return Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
    }
}
