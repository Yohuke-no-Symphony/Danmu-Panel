using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证历史原文与 UI 输入不等待后台拼音，旧房间结果不能回写。</summary>
public sealed class AsyncHistoryLoadingTests
{
    /// <summary>空库或少量原文快速返回时，不产生延迟提示或整页虚构占位。</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task FastEmptyOrSmallReadsDoNotFlashPlaceholders(int count)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration());
        await runtime.Messages.GetGenerationAsync("tab");
        if (count > 0)
        {
            await runtime.TestMessages.AddAsync(Room(), new() { UserName = "观众", Content = "少量历史", Count = count });
        }
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var peakPlaceholders = 0;
        model.ScrollingRows.CollectionChanged += (_, _) => peakPlaceholders = Math.Max(peakPlaceholders,
            model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>().Count());

        await model.SelectRoomAsync(Room());
        await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        Assert.Equal(count, model.ScrollingItems.Count);
        Assert.Equal(0, peakPlaceholders);
        Assert.DoesNotContain(model.ScrollingRows, row => row is MessageLoadingPlaceholderViewModel);
        Assert.False(model.IsLoadingHistory);
    }

    /// <summary>单个面板会填满有界工作线程而不阻塞输入，乱序完成也不改变消息行顺序。</summary>
    [AvaloniaFact]
    public async Task SinglePanelPrefetchesBoundedParallelPresentationWork()
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "中文观众", Content = "重庆银行大家晚上好", Count = 20 });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        try
        {
            await model.SelectRoomAsync(Room());
            var ids = model.Items.Select(item => item.Record.Id).ToArray();
            var workers = runtime.Presentations.WorkerCount;
            Assert.InRange(workers, 2, 4);
            for (var attempt = 0; attempt < 1000 && pinyin.ActiveCalls < workers; attempt++)
            {
                await Task.Delay(5);
            }

            Assert.Equal(workers, pinyin.ActiveCalls);
            Assert.Equal(workers, pinyin.PeakActiveCalls);
            Assert.Equal(20, model.Items.Count);
            Assert.All(model.Items, item => Assert.True(item.ShowOriginal));
            var inputHandled = false;
            await Dispatcher.UIThread.InvokeAsync(() => inputHandled = true, DispatcherPriority.Input);
            Assert.True(inputHandled);
            Assert.DoesNotContain(model.ScrollingRows, row => row is MessageLoadingPlaceholderViewModel);

            pinyin.Release.TrySetResult();
            await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ids, model.Items.Select(item => item.Record.Id));
            Assert.All(model.Items, item => Assert.NotEmpty(item.NameAnnotations));
            Assert.All(model.Items, item => Assert.NotEmpty(item.ContentPinyin));
            Assert.Equal(0, pinyin.ActiveCalls);
            Assert.InRange(pinyin.PeakActiveCalls, 2, workers);
        }
        finally
        {
            pinyin.Release.TrySetResult();
        }
    }

    /// <summary>拼音等待只按对应开关显示无文字矩形，原文和姓名始终可读，完成后原位补齐。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task PinyinWaitingFollowsSettingsWithoutHidingOriginal(bool showName, bool showContent, bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration();
        var settings = configuration.Settings;
        settings.ShowUserPinyin = showName;
        settings.ShowDanmuPinyin = showContent;
        settings.CompactDanmuDisplay = compact;
        settings.AutoScrollLongMessages = false;
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration, pinyin);
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "历史观众", Content = "重庆银行今天营业" });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 650, Height = 380, Content = view };
        try
        {
            window.Show();
            await view.Model.SelectRoomAsync(Room());
            if (showName || showContent)
            {
                await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var nameWaiting = Assert.Single(view.GetVisualDescendants().OfType<PinyinLoadingPlaceholder>(),
                control => control.Classes.Contains("namePinyinPending"));
            var contentWaiting = Assert.Single(view.GetVisualDescendants().OfType<PinyinLoadingPlaceholder>(),
                control => control.Classes.Contains("contentPinyinPending"));
            Assert.Equal(showName, item.IsNamePinyinPending);
            Assert.Equal(showContent, item.IsContentPinyinPending);
            Assert.Equal(showName, nameWaiting.IsEffectivelyVisible);
            Assert.Equal(showContent, contentWaiting.IsEffectivelyVisible);
            Assert.Empty(nameWaiting.GetVisualDescendants().OfType<TextBlock>());
            Assert.Empty(contentWaiting.GetVisualDescendants().OfType<TextBlock>());
            Assert.True(item.ShowOriginal);
            Assert.Contains(view.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                control => control.IsEffectivelyVisible && control.ContentText == "重庆银行今天营业");
            Assert.Contains(view.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                control => control.IsEffectivelyVisible && control.ContentText == "历史观众");
            Assert.DoesNotContain(view.Model.ScrollingRows, row => row is MessageLoadingPlaceholderViewModel);

            settings.ShowUserPinyin = false;
            settings.ShowDanmuPinyin = false;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.False(item.IsNamePinyinPending);
            Assert.False(item.IsContentPinyinPending);
            Assert.False(nameWaiting.IsEffectivelyVisible);
            Assert.False(contentWaiting.IsEffectivelyVisible);
            Assert.True(item.ShowOriginal);

            settings.ShowUserPinyin = showName;
            settings.ShowDanmuPinyin = showContent;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal(showName, nameWaiting.IsEffectivelyVisible);
            Assert.Equal(showContent, contentWaiting.IsEffectivelyVisible);
            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Pump(window);

            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Equal(showName, item.NameAnnotations.Count > 0);
            Assert.Equal(showContent, item.ContentPinyin.Count > 0);
            Assert.False(item.IsNamePinyinPending);
            Assert.False(item.IsContentPinyinPending);
            Assert.False(nameWaiting.IsEffectivelyVisible);
            Assert.False(contentWaiting.IsEffectivelyVisible);
            Assert.True(item.ShowOriginal);
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>显示不足一页的历史后移除剩余占位，后续查询失败仍保留已有真实行。</summary>
    [AvaloniaFact]
    public async Task ExhaustedOrFailedHistoryDoesNotLeavePlaceholderRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "已加载的历史", Count = 3 });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(Room());
        var retained = model.ScrollingItems.ToArray();

        await model.LoadMoreAsync();

        Assert.Equal(retained, model.ScrollingRows.OfType<MessageItemViewModel>());
        Assert.DoesNotContain(model.ScrollingRows, row => row is MessageLoadingPlaceholderViewModel);
        using var external = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        external.Open();
        using var command = external.CreateCommand();
        command.CommandText = "DROP TABLE translation_jobs;";
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<SqliteException>(() => model.LoadMoreAsync());

        Assert.Equal(retained, model.ScrollingRows.OfType<MessageItemViewModel>());
        Assert.DoesNotContain(model.ScrollingRows, row => row is MessageLoadingPlaceholderViewModel);
        Assert.False(model.IsLoadingHistory);
    }

    /// <summary>窗口模型释放会取消尚未显示的延迟提示，回调完成后也不能留下占位。</summary>
    [AvaloniaFact]
    public async Task DisposingAnOutstandingLoadCancelsDelayedRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var loading = model.SelectRoomAsync(Room());
        Assert.Empty(model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>());

        model.Dispose();

        Assert.Empty(model.ScrollingRows);
        Assert.False(model.IsLoadingHistory);
        await loading;
        await Task.Delay(200);
        Assert.Empty(model.ScrollingRows);
    }

    /// <summary>快速读取不闪占位，后台拼音阻塞时仍可显示原文、扩页及处理输入。</summary>
    [AvaloniaFact]
    public async Task HistoryAndInputRemainAvailableWhilePinyinIsBlocked()
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "你好世界", Count = 250 });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 650, Height = 450, Content = view };
        try
        {
            window.Show();
            var initial = view.Model.SelectRoomAsync(Room());
            Assert.True(view.Model.IsLoadingHistory);
            Assert.Empty(view.Model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>());
            await initial.WaitAsync(TimeSpan.FromSeconds(5));
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(100, view.Model.Items.Count);
            Assert.Contains(view.Model.Items, item => item.Record.Content == "你好世界");
            Assert.Contains(view.Model.Items, item => item.IsPresentationPending);

            var more = view.Model.LoadMoreAsync();
            Assert.True(view.Model.IsLoadingHistory);
            Assert.Empty(view.Model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>());
            var duplicate = view.Model.LoadMoreAsync();
            await Task.WhenAll(more, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(200, view.Model.Items.Count);
            Assert.False(view.Model.IsLoadingHistory);
            Assert.DoesNotContain(view.Model.ScrollingRows, row => row is MessageLoadingPlaceholderViewModel);
            var inputHandled = false;
            await Dispatcher.UIThread.InvokeAsync(() => inputHandled = true, DispatcherPriority.Input);
            Assert.True(inputHandled);
            Assert.False(pinyin.Release.Task.IsCompleted);

            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(view.Model.Items, item => Assert.NotEmpty(item.ContentPinyin));
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>房间切换后，旧后台任务完成也不能改变当前消息集合。</summary>
    [AvaloniaFact]
    public async Task SwitchingRoomsRejectsOutstandingPresentationResults()
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "viewer", Content = "旧房间" });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        try
        {
            await model.SelectRoomAsync(Room());
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.SelectRoomAsync(new() { Id = "other", RoomId = "456" });
            pinyin.Release.TrySetResult();
            await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(model.Items);
            Assert.Equal("other", model.SelectedRoom!.Id);
            Assert.False(model.IsLoadingHistory);
        }
        finally
        {
            pinyin.Release.TrySetResult();
        }
    }

    /// <summary>历史集合分批应用时，输入优先级的房间切换会取消剩余旧批次。</summary>
    [AvaloniaFact]
    public async Task SwitchingRoomsDuringHistoryUiBatchRejectsTheRemainingOldRows()
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        var other = new RoomTabState { Id = "other", RoomId = "456" };
        await runtime.TestMessages.AddAsync(Room(), new() { UserName = "old viewer", Content = "旧房间历史", Count = 250 });
        await runtime.TestMessages.AddAsync(other, new() { UserName = "new viewer", Content = "新房间消息", Count = 3 });
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var switchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? switching = null;
        var appliedRows = 0;
        var switchedAfterRows = 0;
        NotifyCollectionChangedEventHandler onChanged = (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || switching is not null ||
                model.SelectedRoom?.Id != "tab")
            {
                return;
            }
            appliedRows += args.NewItems!.Count;
            if (appliedRows == 20)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    switchedAfterRows = appliedRows;
                    switching = model.SelectRoomAsync(other);
                    switchStarted.TrySetResult();
                }, DispatcherPriority.Input);
            }
        };
        try
        {
            await model.SelectRoomAsync(Room());
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.Items.CollectionChanged += onChanged;

            var oldHistory = model.LoadMoreAsync();
            await switchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(oldHistory, switching!).WaitAsync(TimeSpan.FromSeconds(5));
            pinyin.Release.TrySetResult();
            await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(20, switchedAfterRows);
            Assert.Equal("other", model.SelectedRoom!.Id);
            Assert.Equal(3, model.Items.Count);
            Assert.All(model.Items, item => Assert.Equal("other", item.Record.RoomTabId));
            Assert.All(model.ScrollingItems, item => Assert.Equal("新房间消息", item.Record.Content));
            Assert.False(model.IsLoadingHistory);
        }
        finally
        {
            model.Items.CollectionChanged -= onChanged;
            pinyin.Release.TrySetResult();
        }
    }

    /// <summary>加载更早历史及异步补齐注音后，保留同一消息实例和可见行像素位置。</summary>
    [AvaloniaFact]
    public async Task LoadingHistoryAndCompletingPinyinPreserveTheVisibleReadingAnchor()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration();
        configuration.Settings.CompactDanmuDisplay = false;
        configuration.Settings.AutoScrollLongMessages = false;
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration, pinyin);
        var records = Enumerable.Range(0, 310).Select(index => new MessageRecord
        {
            RoomTabId = "tab",
            RoomId = "123",
            Kind = MessageKind.Danmu,
            UserName = "历史观众" + index,
            Content = index % 3 == 0
                ? $"第{index}条消息，重庆银行今天营业，大家晚安。"
                : $"第{index}条历史，今晚一起听歌。",
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index),
            IsTest = true
        }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(records, configuration.Settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 600, Height = 420, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            var presenter = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => ReferenceEquals(presenter.TemplatedParent, viewer));
            presenter.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(),
                new(0, viewer.ScrollBarMaximum.Y * 0.45 - viewer.Offset.Y)));
            Pump(window);
            Assert.True(viewer.Offset.Y > 0 && viewer.Offset.Y < viewer.ScrollBarMaximum.Y);
            Assert.True(view.Model.IsReadingHistory);
            var anchor = list.GetRealizedContainers().Select(container =>
                    (Container: container, Position: container.TranslatePoint(default, viewer)!.Value))
                .Where(value => value.Position.Y + value.Container.Bounds.Height > 0 && value.Position.Y < viewer.Viewport.Height)
                .OrderBy(value => value.Position.Y).First();
            var item = Assert.IsType<MessageItemViewModel>(list.ItemFromContainer(anchor.Container));
            var before = anchor.Position.Y;

            await view.Model.LoadMoreAsync();
            Pump(window);

            Assert.Equal(200, view.Model.ScrollingItems.Count);
            Assert.Same(item, Assert.Single(view.Model.Items, current => current.Record.Id == item.Record.Id));
            var afterLoad = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
            Assert.InRange(Math.Abs(afterLoad.TranslatePoint(default, viewer)!.Value.Y - before), 0, 2);
            Assert.True(item.IsPresentationPending);

            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Pump(window);

            Assert.All(view.Model.Items, current => Assert.False(current.IsPresentationPending));
            Assert.NotEmpty(item.ContentPinyin);
            Assert.Same(item, Assert.Single(view.Model.Items, current => current.Record.Id == item.Record.Id));
            var afterPinyin = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(item));
            Assert.InRange(Math.Abs(afterPinyin.TranslatePoint(default, viewer)!.Value.Y - before), 0, 2);
            Assert.True(view.Model.IsReadingHistory);
            Assert.False(view.Model.IsLoadingHistory);
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>付费面板首屏分批完成后从顶部开始，用户中途滚动时保留阅读位置。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task InitialHistoryBatchesStartAtTopUnlessTheUserScrolls(MessageKind kind, bool userScrolls)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        await runtime.TestMessages.AddAsync(Room(), new()
        {
            Kind = kind, UserName = "viewer", Content = "initial history", GiftName = "鲜花", UnitPrice = 30, Count = 100
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 600, Height = 360, Content = view };
        var appliedMessages = 0;
        var inputHandled = false;
        var inputScheduled = false;
        NotifyCollectionChangedEventHandler onChanged = (_, args) =>
        {
            if (!userScrolls || inputScheduled || args.NewItems is null)
            {
                return;
            }
            appliedMessages += args.NewItems.OfType<MessageItemViewModel>().Count();
            if (appliedMessages >= 20)
            {
                inputScheduled = true;
                Dispatcher.UIThread.Post(() =>
                {
                    var viewer = FindViewer(view);
                    viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End });
                    viewer.Offset = new(0, viewer.ScrollBarMaximum.Y);
                    inputHandled = true;
                }, DispatcherPriority.Input);
            }
        };
        try
        {
            window.Show();
            Pump(window);
            view.Model.ScrollingRows.CollectionChanged += onChanged;

            await view.Model.SelectRoomAsync(Room()).WaitAsync(TimeSpan.FromSeconds(5));
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Pump(window);

            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = FindViewer(view);
            Assert.Equal(100, view.Model.ScrollingItems.Count);
            Assert.All(view.Model.ScrollingRows, row => Assert.IsType<MessageItemViewModel>(row));
            Assert.False(view.Model.IsLoadingHistory);
            Assert.True(viewer.ScrollBarMaximum.Y > 1);
            if (userScrolls)
            {
                Assert.True(inputHandled);
                Assert.True(viewer.Offset.Y > 0);
            }
            else
            {
                Assert.InRange(viewer.Offset.Y, 0, 1);
                Assert.NotNull(list.ContainerFromItem(view.Model.ScrollingItems[0]));
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.ScrollingRows.CollectionChanged -= onChanged;
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>首屏先完成再显示窗口时，行变更和拼音补齐均保留顶部定位。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ShowingPreparedHistoryKeepsTopThroughDeferredRowPresentation(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = new PanelRuntime(new(directory.Path), Configuration(), pinyin);
        await runtime.TestMessages.AddAsync(Room(), new()
        {
            Kind = kind, UserName = "历史观众", Content = "首屏历史内容", GiftName = "鲜花", UnitPrice = 30, Count = 100
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 600, Height = 360, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(Room());
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(100, view.Model.ScrollingItems.Count);
            var first = view.Model.ScrollingItems[0];

            window.Show();
            window.UpdateLayout();
            first.Update(first.Record with { UserName = "更新观众" }, runtime.Configuration.Settings);
            Pump(window);
            var viewer = FindViewer(view);
            Assert.InRange(viewer.Offset.Y, 0, 1);

            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Pump(window);

            Assert.NotEmpty(first.NameAnnotations);
            Assert.InRange(viewer.Offset.Y, 0, 1);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            Assert.NotNull(list.ContainerFromItem(first));
            Assert.True(view.Model.IsReadingHistory);
            Assert.Empty(errors);
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }


    /// <summary>读取被阻塞时仍能真实滚动占位行，替换及注音完成后保留用户最后选择的半行位置。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaceholdersRemainScrollableWhileTheStoreIsBlocked(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration();
        configuration.Settings.CompactDanmuDisplay = compact;
        configuration.Settings.AutoScrollLongMessages = false;
        configuration.Settings.DanmuRegexFilter.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var messages = Enumerable.Range(0, 350).Select(index => new MessageRecord
        {
            Id = $"wheel-history-{index}", RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu,
            IsTest = true, UserName = "历史观众", Content = $"第{index}条历史，重庆银行今天营业。",
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(messages, configuration.Settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 440, Height = 380, Content = view };
        using var external = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        external.Open();
        SqliteTransaction? blocker = null;
        ListBox? list = null;
        ScrollViewer? viewer = null;
        FeedRowViewModel? trackedRow = null;
        var layouts = 0;
        var offsetChanges = 0;
        var inputProbes = 0;
        var fuseTripped = false;

        void OnLayout(object? sender, EventArgs args)
        {
            if (++layouts > 12000 && !fuseTripped)
            {
                fuseTripped = true;
                view.Model.Dispose();
                window.Close();
            }
        }

        void OnOffset(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == ScrollViewer.OffsetProperty && ++offsetChanges > 2048 && !fuseTripped)
            {
                fuseTripped = true;
                view.Model.Dispose();
                window.Close();
            }
        }

        void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action == NotifyCollectionChangedAction.Replace && trackedRow is not null &&
                args.OldItems!.IndexOf(trackedRow) is var index && index >= 0)
            {
                trackedRow = Assert.IsAssignableFrom<FeedRowViewModel>(args.NewItems![index]);
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
            Assert.False(fuseTripped, $"Viewport failed to settle: layouts={layouts}, offsets={offsetChanges}.");
        }

        (Control Container, FeedRowViewModel Row, double Y)[] VisibleRows()
        {
            return list!.GetRealizedContainers().Where(container => container.IsEffectivelyVisible &&
                    list.ItemFromContainer(container) is FeedRowViewModel &&
                    container.TranslatePoint(default, viewer!) is { } position &&
                    position.Y < viewer!.Viewport.Height && position.Y + container.Bounds.Height > 0)
                .Select(container => (container, (FeedRowViewModel)list.ItemFromContainer(container)!,
                    container.TranslatePoint(default, viewer!)!.Value.Y)).OrderBy(value => value.Item3).ToArray();
        }

        window.LayoutUpdated += OnLayout;
        view.Model.ScrollingRows.CollectionChanged += OnRowsChanged;
        try
        {
            window.Show();
            await view.Model.SelectRoomAsync(Room());
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), value => value.Name == "ScrollingMessages");
            viewer = FindViewer(view);
            viewer.PropertyChanged += OnOffset;
            for (var frame = 0; frame < 4; frame++)
            {
                await FrameAsync();
            }
            Assert.False(runtime.IsListening(Room()));
            blocker = external.BeginTransaction();
            var blockedAppend = runtime.Messages.AppendAsync(new()
            {
                Id = "queue-barrier", RoomTabId = "barrier", RoomId = "456", Kind = MessageKind.Danmu, Content = "barrier"
            }, configuration.Settings.Reading);
            var loading = view.Model.LoadMoreAsync();
            Assert.Empty(view.Model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>());
            for (var frame = 0; frame < 40 && !view.Model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>().Any(); frame++)
            {
                await Task.Delay(10);
                await FrameAsync();
            }
            Assert.Equal(3, view.Model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>().Count());
            var point = viewer.TranslatePoint(new Point(80, viewer.Viewport.Height / 2), window)!.Value;
            window.MouseMove(point);
            var wheelUnit = 0d;
            for (var step = 0; step < 4; step++)
            {
                var before = viewer.Offset.Y;
                window.MouseWheel(point, new Vector(0, 1));
                await FrameAsync();
                wheelUnit = before - viewer.Offset.Y;
                Assert.True(wheelUnit > 0);
            }
            var reachedPlaceholders = false;
            for (var step = 0; step < 100 && !reachedPlaceholders; step++)
            {
                var before = viewer.Offset.Y;
                window.MouseWheel(point, new Vector(0, 3));
                await FrameAsync();
                Assert.True(viewer.Offset.Y < before, $"Wheel {step} was ignored or restored: {before} -> {viewer.Offset.Y}.");
                Assert.False(loading.IsCompleted);
                reachedPlaceholders = VisibleRows().Any(value => value.Row is MessageLoadingPlaceholderViewModel);
            }
            Assert.True(reachedPlaceholders, "Real wheel input did not reach the pending history slots.");
            var top = VisibleRows()[0];
            var placeholder = Assert.IsType<MessageLoadingPlaceholderViewModel>(top.Row);
            window.MouseWheel(point, new Vector(0, (-4 - top.Y) / wheelUnit));
            await FrameAsync();
            top = VisibleRows()[0];
            Assert.Same(placeholder, top.Row);
            Assert.InRange(top.Y, -6, -2);
            var slotIndex = view.Model.ScrollingRows.IndexOf(placeholder);
            Assert.InRange(slotIndex, 0, 2);
            trackedRow = placeholder;
            var anchorY = top.Y;
            var selectedOffset = viewer.Offset.Y;
            var probesBeforeQuiet = inputProbes;
            for (var frame = 0; frame < 8; frame++)
            {
                await FrameAsync();
                Assert.InRange(Math.Abs(viewer.Offset.Y - selectedOffset), 0, 1);
            }
            Assert.Equal(8, inputProbes - probesBeforeQuiet);
            Assert.True(view.Model.IsReadingHistory);
            Assert.False(loading.IsCompleted);
            Assert.Equal(3, view.Model.ScrollingRows.OfType<MessageLoadingPlaceholderViewModel>().Count());

            blocker.Rollback();
            blocker.Dispose();
            blocker = null;
            await blockedAppend.WaitAsync(TimeSpan.FromSeconds(10));
            await loading.WaitAsync(TimeSpan.FromSeconds(15));
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(15));
            for (var frame = 0; frame < 4; frame++)
            {
                await FrameAsync();
            }
            var actual = Assert.IsType<MessageItemViewModel>(trackedRow);
            Assert.Equal($"wheel-history-{150 + slotIndex}", actual.Record.Id);
            Assert.Same(actual, view.Model.ScrollingRows[slotIndex]);
            Assert.DoesNotContain(view.Model.ScrollingRows, value => value is MessageLoadingPlaceholderViewModel);
            var settledOffset = viewer.Offset.Y;
            for (var frame = 0; frame < 12; frame++)
            {
                await FrameAsync();
                var container = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(actual));
                Assert.InRange(Math.Abs(container.TranslatePoint(default, viewer)!.Value.Y - anchorY), 0, 1);
                Assert.InRange(Math.Abs(viewer.Offset.Y - settledOffset), 0, 1);
                Assert.True(view.Model.IsReadingHistory);
            }
            Assert.True(inputProbes >= 24);
            Assert.Empty(errors);
        }
        finally
        {
            blocker?.Dispose();
            window.LayoutUpdated -= OnLayout;
            view.Model.ScrollingRows.CollectionChanged -= OnRowsChanged;
            if (viewer is not null)
            {
                viewer.PropertyChanged -= OnOffset;
            }
            view.Model.Dispose();
            window.Close();
        }
    }

    private static ScrollViewer FindViewer(MessageFeedView view)
    {
        var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
        return Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
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
        return new() { Settings = new() { ShowUserPinyin = true, ShowDanmuPinyin = true, MessageRetentionCount = 100 } };
    }

    private static RoomTabState Room()
    {
        return new() { Id = "tab", RoomId = "123" };
    }
}
