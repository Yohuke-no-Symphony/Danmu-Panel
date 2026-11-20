using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证普通历史窗口的稳定边界及解除旧置顶的分页规则。</summary>
public sealed class FeedWindowTests
{
    /// <summary>服务器时间相同也以稳定顺序保留最老记录，扩展窗口再增加一百条。</summary>
    [Fact]
    public async Task EqualTimestampBoundaryRetainsOccurrencesAndExpandsByOneHundred()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (var index = 0; index < 210; index++)
        {
            await store.AppendAsync(Message(time), new());
        }
        var initial = (await store.LoadFeedAsync("tab", MessageKind.Danmu, 100, null)).Records;
        Assert.Equal(100, initial.Count);
        var boundary = initial[0];
        for (var index = 0; index < 3; index++)
        {
            await store.AppendAsync(Message(time), new());
        }

        var retained = (await store.LoadFeedAsync("tab", MessageKind.Danmu, 100, boundary.Id)).Records;
        Assert.Equal(103, retained.Count);
        Assert.Equal(boundary.Id, retained[0].Id);
        Assert.Equal(initial.Select(record => record.Id), retained.Take(initial.Count).Select(record => record.Id));

        var expanded = (await store.LoadFeedAsync("tab", MessageKind.Danmu, retained.Count + 100, boundary.Id)).Records;
        Assert.Equal(203, expanded.Count);
        Assert.Equal(boundary.Id, expanded[100].Id);
        Assert.Equal(203, expanded.Select(record => record.Id).Distinct().Count());
        Assert.Equal(retained.Select(record => record.Id), expanded.Skip(100).Select(record => record.Id));
    }

    /// <summary>解除很旧的置顶不自动读取中间全部历史，继续加载后仍能找到该记录。</summary>
    [Fact]
    public async Task UnpinningOutsideLoadedWindowKeepsPaginationBounded()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var oldest = Message(time.AddMinutes(-1));
        await store.AppendAsync(oldest, new());
        await store.TogglePinAsync(oldest.Id);
        for (var index = 0; index < 10; index++)
        {
            await store.AppendAsync(Message(time.AddSeconds(index)), new());
        }
        var initial = (await store.LoadFeedAsync("tab", MessageKind.Danmu, 3, null)).Records;
        Assert.Equal(oldest.Id, Assert.Single(initial, record => record.IsPriority).Id);
        var boundary = initial.First(record => !record.IsPriority);

        await store.TogglePinAsync(oldest.Id);
        var retained = (await store.LoadFeedAsync("tab", MessageKind.Danmu, 3, boundary.Id)).Records;
        Assert.Equal(3, retained.Count);
        Assert.DoesNotContain(retained, record => record.Id == oldest.Id);
        Assert.Equal(boundary.Id, retained[0].Id);

        var expanded = (await store.LoadFeedAsync("tab", MessageKind.Danmu, 11, boundary.Id)).Records;
        Assert.Equal(11, expanded.Count);
        Assert.Equal(oldest.Id, expanded[0].Id);
        Assert.False(expanded[0].IsPinned);
    }

    /// <summary>保留优先分组，组内同一时间戳按接收顺序稳定追加。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task FeedIsChronologicalWithinPriorityGroupsAndEqualTimestamps(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var records = Enumerable.Range(0, 4)
            .Select(index => Message(time.AddSeconds(index / 2)) with { Kind = kind, TotalPrice = 100 }).ToArray();
        foreach (var record in records)
        {
            await store.AppendAsync(record, new());
        }
        if (kind == MessageKind.Danmu)
        {
            await store.TogglePinAsync(records[1].Id);
        }
        else
        {
            await store.MarkReadAsync(records[1].Id, 1, 100);
        }

        var feed = await store.LoadFeedAsync("tab", kind, 100, null);

        var expected = kind == MessageKind.Danmu
            ? new[] { records[1], records[0], records[2], records[3] }
            : new[] { records[0], records[2], records[3], records[1] };
        Assert.Equal(expected.Select(record => record.Id), feed.Records.Select(record => record.Id));
    }

    /// <summary>同毫秒游标可向两端移动，固定区间在实时追加后保持原边界。</summary>
    [Fact]
    public async Task EqualTimestampPagesStayBoundedWhileNewMessagesArrive()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var records = Enumerable.Range(0, 12).Select(_ => Message(time)).ToArray();
        await store.AppendBatchAsync(records, new());

        var latest = (await store.LoadWindowAsync("tab", MessageKind.Danmu, new(4))).Scrolling;
        Assert.Equal(records.Skip(8).Select(record => record.Id), Ids(latest));
        Assert.True(latest.HasOlder);
        Assert.False(latest.HasNewer);

        var prior = (await store.LoadWindowAsync("tab", MessageKind.Danmu,
            new(4, MessageFeedPageDirection.Before, latest.Entries[0].Cursor))).Scrolling;
        Assert.Equal(records.Skip(4).Take(4).Select(record => record.Id), Ids(prior));
        Assert.True(prior.HasOlder);
        Assert.True(prior.HasNewer);
        var next = (await store.LoadWindowAsync("tab", MessageKind.Danmu,
            new(4, MessageFeedPageDirection.After, prior.Entries[^1].Cursor))).Scrolling;
        Assert.Equal(Ids(latest), Ids(next));

        await store.AppendBatchAsync(Enumerable.Range(0, 20).Select(_ => Message(time)).ToArray(), new());
        var retained = (await store.LoadWindowAsync("tab", MessageKind.Danmu, new(4,
            MessageFeedPageDirection.Range, prior.Entries[0].Cursor, prior.Entries[^1].Cursor))).Scrolling;
        Assert.Equal(Ids(prior), Ids(retained));

        var oldest = (await store.LoadWindowAsync("tab", MessageKind.Danmu,
            new(4, MessageFeedPageDirection.Around, TargetId: records[0].Id))).Scrolling;
        Assert.Equal(records.Take(4).Select(record => record.Id), Ids(oldest));
        Assert.False(oldest.HasOlder);
        Assert.True(oldest.HasNewer);
    }

    /// <summary>付费窗口的已读筛选与可见的完整未读计数保持独立，直接定位不会穿越房间。</summary>
    [Fact]
    public async Task GiftPagesRespectVisibilityReadStateAndRoom()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 10 };
        var expensive = Message(time) with { Kind = MessageKind.Gift, UnitPrice = 10 };
        var hidden = expensive with { Id = "hidden", UnitPrice = 9 };
        var read = expensive with { Id = "read" };
        var anotherRoom = expensive with { Id = "another-room", RoomTabId = "another" };
        await store.AppendBatchAsync([expensive, hidden, read, anotherRoom], options);
        await store.MarkReadAsync(read.Id, 1, 10);

        var unread = await store.LoadWindowAsync("tab", MessageKind.Gift, new(1),
            readFilter: MessageReadFilter.Unread);
        Assert.Equal(expensive.Id, Assert.Single(unread.Scrolling.Entries).Record.Id);
        Assert.Equal(1, unread.UnreadCount);
        var around = new MessageFeedPageRequest(3, MessageFeedPageDirection.Around, TargetId: read.Id);
        var readPage = await store.LoadWindowAsync("tab", MessageKind.Gift, around, readFilter: MessageReadFilter.Read);
        Assert.Equal(read.Id, Assert.Single(readPage.Scrolling.Entries).Record.Id);
        Assert.Equal(1, readPage.UnreadCount);
        var hiddenPage = await store.LoadWindowAsync("tab", MessageKind.Gift, around with { TargetId = hidden.Id });
        Assert.Empty(hiddenPage.Scrolling.Entries);
        var outside = await store.LoadWindowAsync("tab", MessageKind.Gift, around with { TargetId = anotherRoom.Id });
        Assert.Empty(outside.Scrolling.Entries);
    }

    /// <summary>阻塞的历史筛选期间仍可提交与读取最新页，原历史查询继续读取同一个快照。</summary>
    [Fact]
    public async Task HistoryReaderDoesNotBlockWriteOrLatestSnapshot()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var message = Message(time) with { Content = new string('a', 50000) + "!" };
        await store.AppendAsync(message, new(), "configuration");
        var filter = new DanmuRegexFilter(new() { Rules = [new() { Pattern = "^(a+)+$", BlockDisplay = true }] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        filter.PatternTimedOut += _ =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        };
        var history = store.LoadWindowAsync("tab", MessageKind.Danmu,
            new(1, MessageFeedPageDirection.Around, TargetId: message.Id), danmuRegexFilter: filter);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await store.ApplyTranslationAsync(message.Id, 0, "configuration", "new translation")
                .WaitAsync(TimeSpan.FromSeconds(5)));
            var latest = await store.LoadWindowAsync("tab", MessageKind.Danmu, new(1)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("new translation", Assert.Single(latest.Scrolling.Entries).Record.Translation);
            Assert.Empty(latest.PendingTranslationIds);
        }
        finally
        {
            release.TrySetResult();
        }
        var old = await history.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Assert.Single(old.Scrolling.Entries).Record.Translation);
        Assert.Contains(message.Id, old.PendingTranslationIds);
    }

    private static IEnumerable<string> Ids(MessageFeedPage page)
    {
        return page.Entries.Select(entry => entry.Record.Id);
    }

    private static MessageRecord Message(DateTimeOffset time)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu,
            UserUid = 42, UserName = "viewer", Content = "合法的同文弹幕", Time = time
        };
    }
}
