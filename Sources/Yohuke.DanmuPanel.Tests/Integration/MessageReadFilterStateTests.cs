using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证阅读筛选在房间、面板实例及并发历史请求之间保持独立。</summary>
public sealed class MessageReadFilterStateTests
{
    /// <summary>切房保留标签，各面板实例互不影响，新实例默认未读。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ReadSelectionSurvivesRoomChangesAndIsLocalToThePanel(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendBatchAsync([
            Message(kind, 0), Message(kind, 1),
            Message(kind, 2) with { RoomTabId = "second" }, Message(kind, 3) with { RoomTabId = "second" }
        ], new());
        await runtime.Messages.MarkReadAsync("message-1", 1, 30);
        await runtime.Messages.MarkReadAsync("message-3", 1, 30);
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(new() { Id = "tab" });
        Assert.Equal(MessageReadFilter.Unread, model.ReadFilter);
        Assert.Equal("message-0", Assert.Single(model.Items).Record.Id);

        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        Assert.Equal("message-1", Assert.Single(model.Items).Record.Id);
        using var independent = new FeedViewModel(runtime, kind);
        await independent.SelectRoomAsync(new() { Id = "tab" });
        Assert.Equal(MessageReadFilter.Unread, independent.ReadFilter);
        Assert.Equal("message-0", Assert.Single(independent.Items).Record.Id);

        await model.SelectRoomAsync(new() { Id = "second" });
        Assert.Equal(MessageReadFilter.Read, model.ReadFilter);
        Assert.Equal("message-3", Assert.Single(model.Items).Record.Id);
        Assert.Equal(1, model.UnreadCount);
        await model.SelectRoomAsync(null);
        Assert.Equal(MessageReadFilter.Read, model.ReadFilter);
        Assert.Empty(model.Items);
        await model.SelectRoomAsync(new() { Id = "tab" });
        Assert.Equal("message-1", Assert.Single(model.Items).Record.Id);

        using var reopened = new FeedViewModel(runtime, kind);
        await reopened.SelectRoomAsync(new() { Id = "tab" });
        Assert.Equal(MessageReadFilter.Unread, reopened.ReadFilter);
        Assert.Equal("message-0", Assert.Single(reopened.Items).Record.Id);
    }

    /// <summary>历史加载过程中连续切换标签和房间，旧请求不能回写新窗口。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task SwitchingFiltersAndRoomsCancelsOldHistoryWindows(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        foreach (var batch in Enumerable.Range(0, 240).Select(index => Message(kind, index)).Chunk(IMessageStore.MAX_BATCH_COUNT))
        {
            await runtime.Messages.AppendBatchAsync(batch, new());
        }
        await runtime.Messages.MarkAllReadAsync("tab", kind);
        await runtime.Messages.AppendBatchAsync([Message(kind, 240), Message(kind, 241)], new());
        await runtime.Messages.AppendBatchAsync(Enumerable.Range(300, 5)
            .Select(index => Message(kind, index) with { RoomTabId = "second" }).ToArray(), new());
        for (var index = 300; index < 303; index++)
        {
            await runtime.Messages.MarkReadAsync("message-" + index, 1, 30);
        }
        using var model = new FeedViewModel(runtime, kind);
        var errors = new List<Exception>();
        model.Failed += errors.Add;
        await model.SelectRoomAsync(new() { Id = "tab" });
        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        Assert.Equal(100, model.Items.Count);
        await model.LoadMoreAsync();
        Assert.Equal(200, model.Items.Count);

        var loading = model.LoadMoreAsync();
        var unread = model.SelectReadFilterAsync(MessageReadFilter.Unread);
        var room = model.SelectRoomAsync(new() { Id = "second" });
        var read = model.SelectReadFilterAsync(MessageReadFilter.Read);
        await Task.WhenAll(loading, unread, room, read);
        await model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MessageReadFilter.Read, model.ReadFilter);
        Assert.Equal(new[] { "message-302", "message-301", "message-300" }, model.Items.Select(item => item.Record.Id));
        Assert.All(model.Items, item => Assert.Equal("second", item.Record.RoomTabId));
        Assert.All(model.ScrollingRows, row => Assert.IsType<MessageItemViewModel>(row));
        Assert.Equal(2, model.UnreadCount);
        Assert.False(model.IsLoadingHistory);
        Assert.Equal(0, model.ViewportUpdateDepth);
        await model.SelectReadFilterAsync(MessageReadFilter.Unread);
        Assert.Equal(new[] { "message-303", "message-304" }, model.Items.Select(item => item.Record.Id));
        await model.SelectRoomAsync(new() { Id = "tab" });
        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        Assert.Equal(100, model.Items.Count);
        Assert.Empty(errors);
    }

    /// <summary>在已读标签操作全部已读会更新已读窗口，新到消息只增加未读计数。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task MarkAllReadRefreshesTheSelectedReadWindow(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        await runtime.Messages.AppendBatchAsync([Message(kind, 0), Message(kind, 1)], new());
        await runtime.Messages.MarkReadAsync("message-0", 1, 30);
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(new() { Id = "tab" });
        await model.SelectReadFilterAsync(MessageReadFilter.Read);
        await model.MarkAllReadAsync();

        Assert.Equal(new[] { "message-1", "message-0" }, model.Items.Select(item => item.Record.Id));
        Assert.All(model.Items, item => Assert.True(item.Record.IsRead));
        Assert.Equal(0, model.UnreadCount);
        await runtime.Messages.AppendAsync(Message(kind, 2), new());
        await model.RefreshAsync();
        Assert.Equal(2, model.Items.Count);
        Assert.Equal(1, model.UnreadCount);
        await model.SelectReadFilterAsync(MessageReadFilter.Unread);
        Assert.Equal("message-2", Assert.Single(model.Items).Record.Id);
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.MessageRetentionCount = 100;
        return new(new ConfigurationService(directory.Path), configuration);
    }

    private static MessageRecord Message(MessageKind kind, int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "viewer", Content = "谢谢支持", GiftName = "鲜花", GuardLevel = 3,
            UnitPrice = 30, TotalPrice = 30, Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }
}
