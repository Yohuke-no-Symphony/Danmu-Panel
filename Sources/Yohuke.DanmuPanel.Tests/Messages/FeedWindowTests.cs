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

    private static MessageRecord Message(DateTimeOffset time)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu,
            UserUid = 42, UserName = "viewer", Content = "合法的同文弹幕", Time = time
        };
    }
}
