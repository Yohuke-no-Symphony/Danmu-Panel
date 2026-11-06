using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证可配置消息窗口的默认容量、实时调整与历史加载。</summary>
public sealed class MessageRetentionFeedTests
{
    /// <summary>四类消息默认保留最近三百条普通记录，新消息只替换窗口中最早的一条。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task DefaultWindowKeepsThreeHundredRecentMessages(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        await AppendAsync(runtime, kind, 0, 320);
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(Room());

        Assert.Equal(300, model.ScrollingItems.Count);
        Assert.Equal("message 20", model.ScrollingItems[0].Record.Content);
        var oldest = model.ScrollingItems[0];
        var latest = model.ScrollingItems[^1];

        await AppendAsync(runtime, kind, 320, 1);
        await model.RefreshAsync();

        Assert.Equal(300, model.ScrollingItems.Count);
        Assert.DoesNotContain(oldest, model.ScrollingItems);
        Assert.Same(latest, model.ScrollingItems[^2]);
        Assert.Equal("message 320", model.ScrollingItems[^1].Record.Content);
        Assert.NotNull(await runtime.Messages.GetByIdAsync(oldest.Record.Id));
    }

    /// <summary>自定义容量用于各类消息，加载历史仍逐次增加一百条，保存新数量后立即重设窗口。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task CustomCountUpdatesTheCurrentWindowAndKeepsHistoryPagination(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { MessageRetentionCount = 27 }
        });
        await AppendAsync(runtime, kind, 0, 450);
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(Room());
        Assert.Equal(27, model.ScrollingItems.Count);
        var latest = model.ScrollingItems[^1];

        await model.LoadMoreAsync();

        Assert.Equal(127, model.ScrollingItems.Count);
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();
        Assert.Equal(127, model.ScrollingItems.Count);

        runtime.Configuration.Settings.MessageRetentionCount = 65;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(65, model.ScrollingItems.Count);
        Assert.Same(latest, model.ScrollingItems[^1]);
        runtime.Configuration.Settings.MessageRetentionCount = 401;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(401, model.ScrollingItems.Count);
        Assert.Same(latest, model.ScrollingItems[^1]);
        await model.SelectRoomAsync(Room());
        Assert.Equal(401, model.ScrollingItems.Count);
    }

    /// <summary>新设置尚未被定时刷新读取时，加载更多仍以最新配置容量追加一百条。</summary>
    [AvaloniaFact]
    public async Task LoadingMoreImmediatelyAfterSavingUsesTheNewCapacity()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { MessageRetentionCount = 5 }
        });
        await AppendAsync(runtime, MessageKind.Danmu, 0, 450);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(Room());

        runtime.Configuration.Settings.MessageRetentionCount = 200;
        await runtime.ApplySettingsAsync();
        await model.LoadMoreAsync();

        Assert.Equal(300, model.ScrollingItems.Count);
        Assert.Equal("message 150", model.ScrollingItems[0].Record.Content);
    }

    /// <summary>缩小容量时不丢弃正在阅读的历史锚点，重新选择房间后恢复配置容量。</summary>
    [AvaloniaFact]
    public async Task ChangingCountKeepsTheCurrentHistoryBoundary()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { MessageRetentionCount = 5 }
        });
        await AppendAsync(runtime, MessageKind.Danmu, 0, 450);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(Room());
        var anchor = model.ScrollingItems[0];
        model.IsReadingHistory = true;
        await AppendAsync(runtime, MessageKind.Danmu, 450, 6);
        await model.RefreshAsync();
        Assert.Equal(11, model.ScrollingItems.Count);

        runtime.Configuration.Settings.MessageRetentionCount = 2;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(11, model.ScrollingItems.Count);
        Assert.Same(anchor, model.ScrollingItems[0]);
        Assert.True(model.IsReadingHistory);
        await model.LoadMoreAsync();
        Assert.Equal(111, model.ScrollingItems.Count);
        Assert.Same(anchor, model.ScrollingItems[100]);

        await model.SelectRoomAsync(Room());

        Assert.Equal(2, model.ScrollingItems.Count);
        Assert.False(model.IsReadingHistory);
        Assert.NotNull(await runtime.Messages.GetByIdAsync(anchor.Record.Id));
    }

    /// <summary>置顶或未读优先消息额外显示，不挤占配置的普通记录容量。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task PriorityMessagesDoNotConsumeTheConfiguredWindow(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { MessageRetentionCount = 3 }
        });
        await AppendAsync(runtime, kind, 0, 30);
        var priority = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "priority viewer", Content = "priority", TotalPrice = 30,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000030)
        };
        await runtime.Messages.AppendAsync(priority, runtime.Configuration.Settings.Reading);
        if (kind == MessageKind.Danmu)
        {
            await runtime.Messages.TogglePinAsync(priority.Id);
        }
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(Room());

        Assert.Equal(4, model.Items.Count);
        Assert.Equal(priority.Id, Assert.Single(model.Items, item => item.Record.IsPriority).Record.Id);
        Assert.Equal(3, model.Items.Count(item => !item.Record.IsPriority));
    }

    private static RoomTabState Room()
    {
        return new() { Id = "tab", RoomId = "123" };
    }

    private static async Task AppendAsync(PanelRuntime runtime, MessageKind kind, int start, int count)
    {
        var records = Enumerable.Range(start, count).Select(index => new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, IsTest = true,
            Content = $"message {index}", UserName = "viewer", GiftName = "gift",
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(records, runtime.Configuration.Settings.Reading);
        if (kind != MessageKind.Danmu)
        {
            await runtime.Messages.MarkAllReadAsync("tab", kind);
        }
    }
}
