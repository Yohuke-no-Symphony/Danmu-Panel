using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过实际滚动输入验证主弹幕自动向前分页和阅读位置。</summary>
public sealed class AutoHistoryPagingTests
{
    private static readonly TimeSpan TEST_TIMEOUT = TimeSpan.FromSeconds(5);

    /// <summary>一次手势只翻一页，前插保留首个真实行位置，读尽后不再访问更早分页。</summary>
    [AvaloniaFact]
    public async Task UpwardGestureAddsOnePageAndKeepsTheFirstVisibleRow()
    {
        const int SUMMARY_INDEX = 75;
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(retention: 100));
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, MessageKind.Danmu, 250, summaryIndex: SUMMARY_INDEX);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (list, viewer, presenter) = FindRegion(view, "ScrollingMessages");
            Assert.Equal(100, view.Model.ScrollingItems.Count);
            Assert.True(view.Model.HasOlderHistory);
            Assert.True(viewer.Offset.Y > 1);
            var first = view.Model.ScrollingItems[0];
            var pageStarts = 0;
            view.Model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FeedViewModel.IsLoadingHistory) && view.Model.IsLoadingHistory)
                {
                    pageStarts++;
                }
            };

            var gesture = ScrollGestureEventArgs.GetNextFreeId();
            ScrollToUpperEdge(viewer, presenter, gesture);
            window.UpdateLayout();
            Assert.InRange(viewer.Offset.Y, 0, 1);
            var anchor = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(first));
            var before = anchor.TranslatePoint(default, viewer)!.Value.Y;
            Assert.InRange(before, -anchor.Bounds.Height + 1, 1);

            await WaitForPageAsync(view.Model, 200);
            await SettleLayoutAsync(window);

            Assert.Equal(1, pageStarts);
            var summary = view.Model.ScrollingItems[SUMMARY_INDEX - 50];
            Assert.True(summary.IsEventSummary);
            Assert.Equal(MessageId(room, MessageKind.Danmu, SUMMARY_INDEX), summary.Record.Id);
            Assert.Equal(MessageKind.SuperChat, summary.Record.Kind);
            Assert.Same(first, view.Model.ScrollingItems[100]);
            var retained = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(first));
            Assert.InRange(Math.Abs(retained.TranslatePoint(default, viewer)!.Value.Y - before), 0, 1);
            Assert.True(view.Model.IsReadingHistory);

            ScrollToUpperEdge(viewer, presenter, gesture);
            presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gesture));
            await SettleLayoutAsync(window);
            Assert.Equal(1, pageStarts);
            Assert.Equal(200, view.Model.ScrollingItems.Count);

            GestureToTop(viewer, presenter);
            await WaitForPageAsync(view.Model, 250);
            await SettleLayoutAsync(window);
            Assert.Equal(2, pageStarts);
            Assert.Equal(Enumerable.Range(0, 250).Select(index => MessageId(room, MessageKind.Danmu, index)),
                view.Model.ScrollingItems.Select(item => item.Record.Id));
            Assert.False(view.Model.HasOlderHistory);

            GestureToTop(viewer, presenter);
            await SettleLayoutAsync(window);
            Assert.Equal(2, pageStarts);
            Assert.False(view.Model.IsLoadingHistory);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>拖动纵向滚动条到顶并松开后恰好翻一页。</summary>
    [AvaloniaFact]
    public async Task DraggingTheScrollbarThumbToTheTopPagesOnce()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(retention: 100));
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, MessageKind.Danmu, 250);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (_, viewer, _) = FindRegion(view, "ScrollingMessages");
            viewer.AllowAutoHide = false;
            await SettleLayoutAsync(window);
            Assert.True(viewer.Offset.Y > 1);
            var bar = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollBar>(),
                control => control.Orientation == Orientation.Vertical && ReferenceEquals(control.TemplatedParent, viewer));
            var thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
            var start = thumb.TranslatePoint(new(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            var top = new Point(start.X, 1);
            var pageStarts = 0;
            view.Model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FeedViewModel.IsLoadingHistory) && view.Model.IsLoadingHistory)
                {
                    pageStarts++;
                }
            };

            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            try
            {
                window.MouseMove(top, RawInputModifiers.LeftMouseButton);
                await SettleLayoutAsync(window);
                Assert.InRange(viewer.Offset.Y * window.RenderScaling, 0, 1);
                Assert.Equal(0, pageStarts);
            }
            finally
            {
                window.MouseUp(top, MouseButton.Left);
            }

            await WaitForPageAsync(view.Model, 200);
            await SettleLayoutAsync(window);

            Assert.Equal(1, pageStarts);
            Assert.True(view.Model.HasOlderHistory);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>向上滚动仍离顶几像素或程序移到顶端都不翻页，随后明确向上才加载。</summary>
    [AvaloniaFact]
    public async Task NearTopAndProgrammaticTopWaitForAnotherUpwardInput()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(retention: 100));
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, MessageKind.Danmu, 220);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (_, viewer, presenter) = FindRegion(view, "ScrollingMessages");
            var nearTop = 5 / window.RenderScaling;
            Assert.True(viewer.Offset.Y > nearTop);
            var pageStarts = 0;
            view.Model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FeedViewModel.IsLoadingHistory) && view.Model.IsLoadingHistory)
                {
                    pageStarts++;
                }
            };

            var gesture = ScrollGestureEventArgs.GetNextFreeId();
            presenter.RaiseEvent(new ScrollGestureEventArgs(gesture, new(0, nearTop - viewer.Offset.Y)));
            presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gesture));
            await SettleLayoutAsync(window);
            Assert.InRange(viewer.Offset.Y * window.RenderScaling, 3, 7);
            Assert.Equal(0, pageStarts);

            viewer.Offset = default;
            await SettleLayoutAsync(window);
            Assert.InRange(viewer.Offset.Y * window.RenderScaling, 0, 1);
            Assert.Equal(0, pageStarts);
            Assert.Equal(100, view.Model.ScrollingItems.Count);

            GestureToTop(viewer, presenter);
            await WaitForPageAsync(view.Model, 200);

            Assert.Equal(1, pageStarts);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>放得下的页面在程序初次布局时保持原大小，有真实滚轮或键盘输入后才向前翻页。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialLayoutAtTheTopWaitsForWheelOrKeyboardInput(bool keyboard)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(retention: 3));
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, MessageKind.Danmu, 120);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (list, viewer, _) = FindRegion(view, "ScrollingMessages");
            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.Equal(3, view.Model.ScrollingItems.Count);
            Assert.True(view.Model.HasOlderHistory);
            Assert.False(view.Model.IsLoadingHistory);

            var row = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(view.Model.ScrollingItems[1]));
            if (keyboard)
            {
                Assert.True(row.Focus());
                window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
            }
            else
            {
                var point = row.TranslatePoint(new(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseWheel(point, new(0, 1));
            }

            await WaitForPageAsync(view.Model, 103);
            Assert.Equal(MessageId(room, MessageKind.Danmu, 17), view.Model.ScrollingItems[0].Record.Id);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>卡片列表和置顶区域触顶仍保留自己的当前窗口。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, "PinnedMessages")]
    [InlineData(MessageKind.SuperChat, "ScrollingMessages")]
    public async Task OtherRegionsDoNotAutomaticallyPage(MessageKind kind, string region)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration(retention: 100);
        configuration.Settings.MessageSorting = configuration.Settings.MessageSorting with
        {
            SuperChat = new() { Unread = MessageSortOrder.NewestFirst }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, kind, 150);
        if (kind == MessageKind.Danmu)
        {
            for (var index = 0; index < 24; index++)
            {
                await runtime.Messages.TogglePinAsync(MessageId(room, kind, index));
            }
        }
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (_, viewer, presenter) = FindRegion(view, region);
            var ordinary = view.Model.ScrollingItems.ToArray();
            var pinned = view.Model.PinnedItems.ToArray();
            Assert.Equal(100, ordinary.Length);
            Assert.True(view.Model.HasOlderHistory);
            Assert.True(viewer.ScrollBarMaximum.Y > 1);
            var attemptedPage = false;
            view.Model.PropertyChanged += (_, args) =>
            {
                attemptedPage |= args.PropertyName == nameof(FeedViewModel.IsLoadingHistory) && view.Model.IsLoadingHistory;
            };

            GestureToTop(viewer, presenter);
            await SettleLayoutAsync(window);

            Assert.InRange(viewer.Offset.Y, 0, 1);
            Assert.False(attemptedPage);
            Assert.Equal(ordinary, view.Model.ScrollingItems);
            Assert.Equal(pinned, view.Model.PinnedItems);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>慢速翻页期间重复触顶只读取一次，导航和清空都能及时取消旧页。</summary>
    [AvaloniaTheory]
    [InlineData("switchRoom")]
    [InlineData("backToLatest")]
    [InlineData("scrollToLatest")]
    [InlineData("clear")]
    public async Task NavigatingWhileAnAutomaticPageIsBlockedCancelsItsResults(string action)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(retention: 100));
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var otherRoom = new RoomTabState { Id = "other", RoomId = "456" };
        const string BLOCKING_ROOM = "blocked-reader";
        await SeedAsync(runtime, room, MessageKind.Danmu, 220);
        await SeedAsync(runtime, otherRoom, MessageKind.Danmu, 5);
        await runtime.Messages.AppendTestMessagesAsync([new()
        {
            RoomTabId = BLOCKING_ROOM, Kind = MessageKind.Danmu, IsTest = true, Content = new string('a', 50000) + "!"
        }], runtime.Configuration.Settings.Reading);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filter = new DanmuRegexFilter(new() { Rules = [new() { Pattern = "^(a+)+$", BlockDisplay = true }] });
        filter.PatternTimedOut += _ =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        };
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (_, viewer, presenter) = FindRegion(view, "ScrollingMessages");
            var historyRequest = new MessageFeedPageRequest(1, MessageFeedPageDirection.Before,
                new MessageFeedCursor(long.MaxValue, long.MaxValue));
            var blockedRead = runtime.Messages.LoadWindowAsync(BLOCKING_ROOM, MessageKind.Danmu, historyRequest,
                danmuRegexFilter: filter);
            await entered.Task.WaitAsync(TEST_TIMEOUT);

            var placeholders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Model.ScrollingRows.CollectionChanged += (_, args) =>
            {
                if (args.NewItems?.OfType<MessageLoadingPlaceholderViewModel>().Any() == true)
                {
                    placeholders.TrySetResult();
                }
            };
            var pageStarts = 0;
            view.Model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FeedViewModel.IsLoadingHistory) && view.Model.IsLoadingHistory)
                {
                    pageStarts++;
                }
            };
            GestureToTop(viewer, presenter);
            await placeholders.Task.WaitAsync(TEST_TIMEOUT);
            Assert.True(view.Model.IsLoadingHistory);
            GestureToTop(viewer, presenter);
            GestureToTop(viewer, presenter);
            await SettleLayoutAsync(window);
            Assert.Equal(1, pageStarts);

            var navigation = action switch
            {
                "switchRoom" => view.Model.SelectRoomAsync(otherRoom),
                "backToLatest" => view.Header.BackToLatestCommand.ExecuteAsync(null),
                "scrollToLatest" => ScrollToLatestAsync(),
                "clear" => view.Model.ClearAsync(room),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            await navigation.WaitAsync(TEST_TIMEOUT);
            Assert.False(blockedRead.IsCompleted);
            Assert.False(view.Model.IsLoadingHistory);
            Assert.All(view.Model.ScrollingRows, row => Assert.IsType<MessageItemViewModel>(row));

            release.SetResult();
            await blockedRead.WaitAsync(TEST_TIMEOUT);
            await runtime.Messages.LoadWindowAsync(BLOCKING_ROOM, MessageKind.Danmu, historyRequest).WaitAsync(TEST_TIMEOUT);
            await SettleLayoutAsync(window);

            if (action == "switchRoom")
            {
                Assert.Equal(otherRoom.Id, view.Model.SelectedRoom!.Id);
                Assert.Equal(5, view.Model.ScrollingItems.Count);
                Assert.All(view.Model.ScrollingItems, item => Assert.Equal(otherRoom.Id, item.Record.RoomTabId));
            }
            else if (action == "clear")
            {
                Assert.Empty(view.Model.ScrollingItems);
                Assert.False(view.Model.HasOlderHistory);
            }
            else
            {
                Assert.Equal(100, view.Model.ScrollingItems.Count);
                Assert.Equal(MessageId(room, MessageKind.Danmu, 120), view.Model.ScrollingItems[0].Record.Id);
                Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
                Assert.False(view.Model.IsReadingHistory);
            }
            Assert.Equal(0, view.Model.ViewportUpdateDepth);
            Assert.Empty(errors);

            async Task ScrollToLatestAsync()
            {
                var gesture = ScrollGestureEventArgs.GetNextFreeId();
                presenter.RaiseEvent(new ScrollGestureEventArgs(gesture, new(0, viewer.ScrollBarMaximum.Y + 20)));
                presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gesture));
                await WaitForPageAsync(view.Model, 100);
            }
        }
        finally
        {
            release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>读取失败后保留当前页，再次向上输入可在原地重试。</summary>
    [AvaloniaFact]
    public async Task ANewGestureRetriesAfterAnAutomaticPageFails()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(retention: 100));
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await SeedAsync(runtime, room, MessageKind.Danmu, 220);
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception =>
        {
            errors.Add(exception);
            error.TrySetResult(exception);
        });
        var window = new Window { Width = 560, Height = 380, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            await SettleLayoutAsync(window);
            var (_, viewer, presenter) = FindRegion(view, "ScrollingMessages");
            var originals = view.Model.ScrollingItems.ToArray();
            using var external = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = directory.DatabasePath, Pooling = false
            }.ToString());
            external.Open();
            using var command = external.CreateCommand();
            command.CommandText = "ALTER TABLE message_records RENAME TO unavailable_records;";
            command.ExecuteNonQuery();
            try
            {
                GestureToTop(viewer, presenter);
                Assert.IsType<SqliteException>(await error.Task.WaitAsync(TEST_TIMEOUT));
            }
            finally
            {
                command.CommandText = "ALTER TABLE unavailable_records RENAME TO message_records;";
                command.ExecuteNonQuery();
            }
            await SettleLayoutAsync(window);
            Assert.False(view.Model.IsLoadingHistory);
            Assert.All(view.Model.ScrollingRows, row => Assert.IsType<MessageItemViewModel>(row));
            Assert.Equal(originals, view.Model.ScrollingItems);
            Assert.InRange(viewer.Offset.Y, 0, 1);

            GestureToTop(viewer, presenter);
            await WaitForPageAsync(view.Model, 200);

            Assert.Equal(originals, view.Model.ScrollingItems.Skip(100));
            Assert.Single(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static PanelConfiguration Configuration(int retention)
    {
        var configuration = new PanelConfiguration();
        configuration.Settings.MessageRetentionCount = retention;
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.ShowUserPinyin = false;
        configuration.Settings.ShowDanmuPinyin = false;
        configuration.Settings.ShowUserAvatars = false;
        return configuration;
    }

    private static Task<int> SeedAsync(PanelRuntime runtime, RoomTabState room, MessageKind kind, int count, int? summaryIndex = null)
    {
        var messages = Enumerable.Range(0, count).Select(index => new MessageRecord
        {
            Id = MessageId(room, kind, index), RoomTabId = room.Id, RoomId = room.RoomId,
            Kind = index == summaryIndex ? MessageKind.SuperChat : kind,
            IsTest = true, UserName = "viewer", Content = $"消息 {index}", TotalPrice = 30,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        return runtime.Messages.AppendTestMessagesAsync(messages, runtime.Configuration.Settings.Reading);
    }

    private static string MessageId(RoomTabState room, MessageKind kind, int index)
    {
        return $"{room.Id}-{kind}-{index}";
    }

    private static (ListBox List, ScrollViewer Viewer, ScrollContentPresenter Presenter) FindRegion(MessageFeedView view, string name)
    {
        var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == name);
        var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(),
            control => ReferenceEquals(control.TemplatedParent, list));
        var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
            control => ReferenceEquals(control.TemplatedParent, viewer));
        return (list, viewer, presenter);
    }

    private static void GestureToTop(ScrollViewer viewer, ScrollContentPresenter presenter)
    {
        var gesture = ScrollGestureEventArgs.GetNextFreeId();
        ScrollToUpperEdge(viewer, presenter, gesture);
        presenter.RaiseEvent(new ScrollGestureEndedEventArgs(gesture));
    }

    private static void ScrollToUpperEdge(ScrollViewer viewer, ScrollContentPresenter presenter, int gesture)
    {
        presenter.RaiseEvent(new ScrollGestureEventArgs(gesture, new(0, -viewer.Offset.Y - 20)));
    }

    private static async Task WaitForPageAsync(FeedViewModel model, int count)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FeedViewModel.IsLoadingHistory))
            {
                CheckPage();
            }
        }
        void CheckPage()
        {
            if (!model.IsLoadingHistory && model.ScrollingItems.Count == count)
            {
                completed.TrySetResult();
            }
        }
        model.PropertyChanged += OnChanged;
        try
        {
            CheckPage();
            await completed.Task.WaitAsync(TEST_TIMEOUT);
        }
        finally
        {
            model.PropertyChanged -= OnChanged;
        }
    }

    private static async Task SettleLayoutAsync(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
