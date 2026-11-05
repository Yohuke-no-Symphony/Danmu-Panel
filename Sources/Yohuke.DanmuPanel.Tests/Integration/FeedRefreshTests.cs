using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证高频消息通知不会淹没 UI 队列，且合并刷新不会遗漏后续变化。</summary>
public sealed class FeedRefreshTests(ITestOutputHelper output)
{
    /// <summary>重读一页未变化的数据库记录时不向旧消息行广播属性变化。</summary>
    [AvaloniaFact]
    public async Task UnchangedRefreshDoesNotNotifyExistingRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "unchanged", Count = 100
        });
        await model.SelectRoomAsync(room);
        await model.WaitForPresentationAsync();
        var originals = model.Items.ToArray();
        var changes = 0;
        foreach (var item in originals)
        {
            item.PropertyChanged += (_, _) => changes++;
        }

        await model.RefreshAsync();
        await model.WaitForPresentationAsync();

        Assert.Equal(0, changes);
        Assert.Equal(originals, model.Items.ToArray());
    }

    /// <summary>单条弹幕置顶时保留其他行的绑定与已加载图片。</summary>
    [AvaloniaFact]
    public async Task PinningOneMessageDoesNotNotifyOtherRows()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "unchanged", Count = 100
        });
        await model.SelectRoomAsync(room);
        await model.WaitForPresentationAsync();
        var target = model.Items[50];
        var changed = new HashSet<MessageItemViewModel>();
        foreach (var item in model.Items)
        {
            item.PropertyChanged += (_, _) => changed.Add(item);
        }

        await runtime.Messages.TogglePinAsync(target.Record.Id);
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();

        Assert.Same(target, Assert.Single(changed));
        Assert.Same(target, Assert.Single(model.PinnedItems));
    }

    /// <summary>突发通知的调度分配保持有界，不为每条通知创建 UI 操作和闭包。</summary>
    [AvaloniaFact]
    public async Task ChangeBurstHasBoundedDispatchAllocations()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        Dispatcher.UIThread.RunJobs();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1000; index++)
        {
            runtime.NotifyAppearanceChanged();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1000 notifications allocated {allocated} bytes for UI dispatch.");
        Assert.True(allocated < 32768, $"A burst of 1000 notifications allocated {allocated} bytes for UI dispatch.");
    }

    /// <summary>没有选中阅读房间时，变化通知无需刷新显示属性。</summary>
    [AvaloniaFact]
    public async Task UnselectedFeedDoesNotQueueRefresh()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var updates = 0;
        model.PropertyChanged += (_, _) => updates++;

        runtime.NotifyAppearanceChanged();

        await Task.Delay(250);
        Assert.Equal(0, updates);
    }

    /// <summary>本地消息增删只发布消息变化，不要求工作区重算监听状态。</summary>
    [AvaloniaFact]
    public async Task TestMessageChangesDoNotPublishWorkspaceChanges()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        var workspaceChanges = 0;
        runtime.Changed += _ => workspaceChanges++;

        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "burst", Count = 100
        });
        await runtime.TestMessages.ClearAsync(room);

        Assert.Equal(0, workspaceChanges);
    }

    /// <summary>消息通知仍能自动更新当前房间，其他房间的消息不会触发该列表刷新。</summary>
    [AvaloniaFact]
    public async Task MessageChangesRefreshOnlyTheSelectedRoom()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await model.SelectRoomAsync(room);
        var updates = 0;
        model.PropertyChanged += (_, _) => updates++;

        await runtime.TestMessages.AddAsync(new() { Id = "other", RoomId = "456" }, new()
        {
            Kind = MessageKind.Danmu, UserName = "other viewer", Content = "other room"
        });
        await Task.Delay(250);
        Assert.Equal(0, updates);

        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "selected room"
        });
        await WaitUntilAsync(() => model.Items.Count == 1);
        Assert.Equal("selected room", Assert.Single(model.Items).Detail);

        await runtime.TestMessages.ClearAsync(room);
        await WaitUntilAsync(() => model.Items.Count == 0);
    }

    /// <summary>刷新期间收到的新变化仍会补刷，并显示期间提交的下一条消息。</summary>
    [AvaloniaFact]
    public async Task ChangeDuringRefreshSchedulesOneFollowUp()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await model.SelectRoomAsync(room);
        Task<MessageAppendResult>? append = null;
        var notifyChanged = runtime.NotifyAppearanceChanged;
        var appendMessage = runtime.Messages.AppendAsync;
        model.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(FeedViewModel.HasScrollingItems) && sender is FeedViewModel { HasScrollingItems: true })
            {
                append = appendMessage(new()
                {
                    RoomTabId = room.Id, Kind = MessageKind.Danmu, Content = "arrived during refresh"
                }, new());
                for (var index = 0; index < 1000; index++)
                {
                    notifyChanged();
                }
            }
        };

        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "viewer", Content = "first"
        });
        await WaitUntilAsync(() => model.Items.Count == 2);

        Assert.NotNull(append);
        await append;
        Assert.Contains(model.Items, item => item.Detail == "arrived during refresh");
    }

    /// <summary>已经入队的刷新在释放后失效，不再读写消息列表。</summary>
    [AvaloniaFact]
    public async Task DisposeInvalidatesQueuedRefresh()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var model = new FeedViewModel(runtime, MessageKind.Danmu);
        try
        {
            await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            var updates = 0;
            model.PropertyChanged += (_, _) => updates++;

            runtime.NotifyAppearanceChanged();
            model.Dispose();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(200);

            Assert.Equal(0, updates);
        }
        finally
        {
            model.Dispose();
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new ConfigurationService(directory.Path), new PanelConfiguration());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The scheduled message refresh did not complete within the test timeout.");
    }
}
