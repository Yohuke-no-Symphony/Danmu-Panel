using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证正则屏蔽的消息类型边界、分页和恢复行为。</summary>
public sealed class DanmuRegexFeedTests
{
    /// <summary>默认不隐藏消息，开启屏蔽后仍保留原记录及置顶，其他消息类型和未读不受影响。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task FilterOnlyHidesDanmuAndCanRestorePinnedMessages(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var translation = Message("菜【同传内容】", 0, kind) with { IsTest = true };
        var ordinary = Message("我喜欢【这首歌】的歌词", 1, kind);
        await runtime.Messages.AppendAsync(translation, new());
        await runtime.Messages.AppendAsync(ordinary, new());
        if (kind == MessageKind.Danmu)
        {
            await runtime.Messages.TogglePinAsync(translation.Id);
        }
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });

        Assert.Equal(2, model.Items.Count);
        if (kind == MessageKind.Danmu)
        {
            Assert.Equal(translation.Id, Assert.Single(model.PinnedItems).Record.Id);
        }

        runtime.Configuration.Settings.DanmuRegexFilter.Enabled = true;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(kind == MessageKind.Danmu ? 1 : 2, model.Items.Count);
        Assert.NotNull(await runtime.Messages.GetByIdAsync(translation.Id));
        var retained = model.Items.Single(item => item.Record.Id == ordinary.Id);
        if (kind == MessageKind.Danmu)
        {
            Assert.Empty(model.PinnedItems);
        }
        else
        {
            Assert.Equal(2, model.UnreadCount);
        }

        runtime.Configuration.Settings.DanmuRegexFilter.Enabled = false;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(2, model.Items.Count);
        Assert.Same(retained, model.Items.Single(item => item.Record.Id == ordinary.Id));
        if (kind == MessageKind.Danmu)
        {
            Assert.Equal(translation.Id, Assert.Single(model.PinnedItems).Record.Id);
        }

        runtime.Configuration.Settings.DanmuRegexFilter.Enabled = true;
        runtime.Configuration.Settings.DanmuRegexFilter.Patterns = ["^我喜欢"];
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(kind == MessageKind.Danmu ? 1 : 2, model.Items.Count);
        Assert.Contains(model.Items, item => item.Record.Id == translation.Id);
    }

    /// <summary>被屏蔽消息不占页数或置顶容量，历史边界和加载更多保持连续。</summary>
    [AvaloniaFact]
    public async Task HiddenMessagesDoNotConsumePageSlotsOrHistoryBoundary()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new()
        {
            Settings = new() { DanmuRegexFilter = new() { Enabled = true } }
        });
        for (var index = 0; index < 120; index++)
        {
            await runtime.Messages.AppendAsync(Message($"文字{index}", index), new());
            await runtime.Messages.AppendAsync(Message($"菜【同传{index}】", index), new());
        }
        for (var index = 120; index < 230; index++)
        {
            await runtime.Messages.AppendAsync(Message("【同传】", index), new());
        }
        var hiddenPinned = Message("菜【置顶同传】", 230);
        var visiblePinned = Message("保留置顶", 231);
        await runtime.Messages.AppendAsync(hiddenPinned, new());
        await runtime.Messages.AppendAsync(visiblePinned, new());
        await runtime.Messages.TogglePinAsync(hiddenPinned.Id);
        await runtime.Messages.TogglePinAsync(visiblePinned.Id);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });

        Assert.Equal(100, model.ScrollingItems.Count);
        Assert.Equal("文字20", model.ScrollingItems[0].Record.Content);
        Assert.Equal("文字119", model.ScrollingItems[^1].Record.Content);
        Assert.Equal(visiblePinned.Id, Assert.Single(model.PinnedItems).Record.Id);

        model.IsReadingHistory = true;
        await runtime.Messages.AppendAsync(Message("新弹幕", 232), new());
        await runtime.Messages.AppendAsync(Message("【新的同传】", 233), new());
        await model.RefreshAsync();

        Assert.Equal(101, model.ScrollingItems.Count);
        Assert.Equal("文字20", model.ScrollingItems[0].Record.Content);

        await model.LoadMoreAsync();

        Assert.Equal(121, model.ScrollingItems.Count);
        Assert.Equal("文字0", model.ScrollingItems[0].Record.Content);
        Assert.Equal("新弹幕", model.ScrollingItems[^1].Record.Content);
        Assert.Equal(visiblePinned.Id, Assert.Single(model.PinnedItems).Record.Id);
    }

    /// <summary>正则屏蔽和纯表情隐藏独立生效，关闭一个不会撤销另一个。</summary>
    [AvaloniaFact]
    public async Task RegexAndEmoticonFiltersComposeIndependently()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new PanelConfiguration
        {
            Settings = new() { HideEmoticonDanmu = true, DanmuRegexFilter = new() { Enabled = true } }
        });
        var emoticon = Message("😀", 0);
        var translation = Message("【同传】", 1);
        var ordinary = Message("普通弹幕", 2);
        foreach (var record in new[] { emoticon, translation, ordinary })
        {
            await runtime.Messages.AppendAsync(record, new());
        }
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
        Assert.Equal(ordinary.Id, Assert.Single(model.Items).Record.Id);

        runtime.Configuration.Settings.DanmuRegexFilter.Enabled = false;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(2, model.Items.Count);
        Assert.DoesNotContain(model.Items, item => item.Record.Id == emoticon.Id);

        runtime.Configuration.Settings.HideEmoticonDanmu = false;
        runtime.Configuration.Settings.DanmuRegexFilter.Enabled = true;
        await runtime.ApplySettingsAsync();
        await model.RefreshAsync();

        Assert.Equal(2, model.Items.Count);
        Assert.DoesNotContain(model.Items, item => item.Record.Id == translation.Id);
    }

    /// <summary>正则超时不能打断 SQLite 查询，暂停后仍能继续读取和接收消息。</summary>
    [Fact]
    public async Task TimedOutRuleDoesNotInterruptDatabaseReadsOrWrites()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var filter = new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^(a+)+$", "^广告"] });
        await store.AppendAsync(Message(new string('a', 100_000) + "!", 0), new());
        await store.AppendAsync(Message(new string('a', 100_001) + "!", 1), new());
        await store.AppendAsync(Message("广告内容", 2), new());
        var timedOut = new List<int>();
        filter.PatternTimedOut += timedOut.Add;

        var initial = await store.LoadFeedAsync("tab", MessageKind.Danmu, 100, null, danmuRegexFilter: filter);
        Assert.Equal(2, initial.Records.Count);
        Assert.Equal(1, Assert.Single(timedOut));

        var ordinary = Message("正常聊天", 3);
        await store.AppendAsync(ordinary, new());
        var refreshed = await store.LoadFeedAsync("tab", MessageKind.Danmu, 100, null, danmuRegexFilter: filter);

        Assert.Equal(3, refreshed.Records.Count);
        Assert.Contains(refreshed.Records, item => item.Id == ordinary.Id);
        Assert.Single(timedOut);
    }

    private static MessageRecord Message(string content, int seconds, MessageKind kind = MessageKind.Danmu)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", UserName = "viewer", Kind = kind,
            Content = content, GiftName = content,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000).AddSeconds(seconds)
        };
    }
}
