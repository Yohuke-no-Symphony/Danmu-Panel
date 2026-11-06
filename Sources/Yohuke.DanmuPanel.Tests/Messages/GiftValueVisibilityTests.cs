using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证礼物价值门槛在列表分页及未读统计之前生效，并保留原始记录。</summary>
public sealed class GiftValueVisibilityTests
{
    /// <summary>金额使用精确小数比较，低于门槛的已读和未读礼物都隐藏，等于门槛仍显示。</summary>
    [Theory]
    [InlineData("0.1", "0.0999999999999999999999999999", false)]
    [InlineData("0.1", "0.1", true)]
    [InlineData("0.1", "0.1000000000000000000000000001", true)]
    [InlineData("0", "0", true)]
    public async Task ThresholdFiltersReadAndUnreadGiftsWithDecimalPrecision(string minimumText,
        string priceText, bool visible)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var price = decimal.Parse(priceText, CultureInfo.InvariantCulture);
        var options = new MessageReadOptions
        {
            GiftAutoPinMinimum = decimal.Parse(minimumText, CultureInfo.InvariantCulture)
        };
        await store.AppendBatchAsync([Gift("unread", 0, price), Gift("read", 1, price)], options);
        await store.MarkReadAsync("read", 1, price);

        var snapshot = await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null);

        Assert.Equal(visible ? 2 : 0, snapshot.Records.Count);
        Assert.Equal(visible ? 1 : 0, snapshot.UnreadCount);
        var records = await store.LoadPageAsync("tab", MessageKind.Gift, 0, 100);
        Assert.Equal(2, records.Count);
        Assert.False(records.Single(record => record.Id == "unread").IsRead);
        Assert.True(records.Single(record => record.Id == "read").IsRead);
        Assert.All(records, record => Assert.Equal(price, record.TotalPrice));
        if (visible)
        {
            Assert.Equal(new[] { "unread", "read" }, snapshot.Records.Select(record => record.Id));
            Assert.False(snapshot.Records[1].IsPriority);
        }
    }

    /// <summary>更近的低价历史不占页数，保留边界及加载更多均只计算可见的已读礼物。</summary>
    [Fact]
    public async Task HiddenGiftsDoNotConsumeHistoryPagesOrRetainedBoundary()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 10 };
        var messages = Enumerable.Range(0, 300).Select(index => Gift("visible-" + index, index, 10))
            .Concat(Enumerable.Range(300, 200).Select(index => Gift("hidden-" + index, index, 1))).ToArray();
        foreach (var batch in messages.Chunk(IMessageStore.MAX_BATCH_COUNT))
        {
            await store.AppendBatchAsync(batch, options);
        }
        await store.MarkAllReadAsync("tab", MessageKind.Gift);

        var initial = await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null);

        Assert.Equal(Enumerable.Range(200, 100).Select(index => "visible-" + index), initial.Records.Select(record => record.Id));
        Assert.Equal(0, initial.UnreadCount);
        await store.AppendBatchAsync([Gift("visible-300", 500, 10), Gift("hidden-new", 501, 1)], options);
        await store.MarkAllReadAsync("tab", MessageKind.Gift);
        var retained = await store.LoadFeedAsync("tab", MessageKind.Gift, 100, initial.Records[0].Id);
        Assert.Equal(Enumerable.Range(200, 101).Select(index => "visible-" + index), retained.Records.Select(record => record.Id));

        var expanded = await store.LoadFeedAsync("tab", MessageKind.Gift, 201, initial.Records[0].Id);

        Assert.Equal(Enumerable.Range(100, 201).Select(index => "visible-" + index), expanded.Records.Select(record => record.Id));
        Assert.Equal(502, (await store.LoadPageAsync("tab", MessageKind.Gift, 0, 1000)).Count);
    }

    /// <summary>礼物过滤复用已验证的正文缓存，外部金额更新后仍立即刷新可见性和计数。</summary>
    [Fact]
    public async Task FilteredReadsReusePayloadsAndObserveExternalValueChanges()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 10 };
        await store.AppendBatchAsync([Gift("hidden", 0, 5), Gift("visible", 1, 10)], options);
        var first = Assert.Single((await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null)).Records);
        var rowsRead = store.PayloadRowsRead;
        var bytesRead = store.PayloadTextBytesRead;

        Assert.Same(first, Assert.Single((await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null)).Records));
        Assert.Equal(rowsRead, store.PayloadRowsRead);
        Assert.Equal(bytesRead, store.PayloadTextBytesRead);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE message_records SET priority=1,
                payload=json_set(payload,'$.TotalPrice',10,'$.UnitPrice',10,'$.IsPriority',json('true'))
            WHERE id='hidden';
            """;
        command.ExecuteNonQuery();

        var changed = await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null);

        Assert.Equal(new[] { "hidden", "visible" }, changed.Records.Select(record => record.Id));
        Assert.Equal(2, changed.UnreadCount);
        Assert.Same(first, changed.Records[1]);
        rowsRead = store.PayloadRowsRead;
        bytesRead = store.PayloadTextBytesRead;
        var warm = await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null);
        Assert.Equal(changed.Records, warm.Records);
        Assert.Equal(rowsRead, store.PayloadRowsRead);
        Assert.Equal(bytesRead, store.PayloadTextBytesRead);
    }

    /// <summary>礼物显示门槛不影响普通弹幕、SC 或大航海的显示和未读。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Guard)]
    public async Task GiftThresholdDoesNotFilterOtherMessageKinds(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 1000000 };
        await store.AppendBatchAsync([Gift("gift", 0, 10), Gift("other", 1, 10) with { Kind = kind }], options);

        var snapshot = await store.LoadFeedAsync("tab", kind, 100, null);

        Assert.Equal("other", Assert.Single(snapshot.Records).Id);
        Assert.Equal(kind == MessageKind.Danmu ? 0 : 1, snapshot.UnreadCount);
        Assert.Empty((await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null)).Records);
        Assert.NotNull(await store.GetByIdAsync("gift"));
    }

    private static MessageRecord Gift(string id, int sequence, decimal price)
    {
        return new()
        {
            Id = id, RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            UserUid = 42, UserName = "viewer", GiftId = "flower", GiftName = "鲜花",
            UnitPrice = price, TotalPrice = price, Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + sequence)
        };
    }
}
