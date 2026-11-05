using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证展开历史后的重复刷新只补读缺失或变化的 SQLite 正文。</summary>
public sealed class MessageStorePayloadReadTests(ITestOutputHelper output)
{
    /// <summary>五千条历史预热后，筛选开关均不会让刷新重新读取原有正文。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmHistoryReadsOnlyNewOrChangedPayloads(bool useFilters)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        for (var batch = 0; batch < 10; batch++)
        {
            var records = Enumerable.Range(batch * 500, 500).Select(index => Message(index) with { IsTest = true }).ToArray();
            await store.AppendTestMessagesAsync(records, new());
        }
        var filter = useFilters ? new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^广告"] }) : null;
        var first = await store.LoadFeedAsync("room", MessageKind.Danmu, 5000, null,
            hideEmoticonDanmu: useFilters, danmuRegexFilter: filter);
        Assert.Equal(5000, first.Records.Count);
        var firstReads = store.PayloadRowsRead;
        var firstBytes = store.PayloadTextBytesRead;
        var warm = await store.LoadFeedAsync("room", MessageKind.Danmu, 5000, first.Records[0].Id,
            hideEmoticonDanmu: useFilters, danmuRegexFilter: filter);

        Assert.Equal(firstReads, store.PayloadRowsRead);
        Assert.Equal(firstBytes, store.PayloadTextBytesRead);
        Assert.Same(first.Records[2000], warm.Records[2000]);
        await store.AppendAsync(Message(5000), new());
        var appended = await store.LoadFeedAsync("room", MessageKind.Danmu, 5000, first.Records[0].Id,
            hideEmoticonDanmu: useFilters, danmuRegexFilter: filter);
        Assert.Equal(5001, appended.Records.Count);
        Assert.Equal(firstReads + 1, store.PayloadRowsRead);

        await store.TogglePinAsync(first.Records[2000].Id);
        var changed = await store.LoadFeedAsync("room", MessageKind.Danmu, 5000, first.Records[0].Id,
            hideEmoticonDanmu: useFilters, danmuRegexFilter: filter);
        Assert.Equal(firstReads + 2, store.PayloadRowsRead);
        Assert.Equal(first.Records[2000].Id, changed.Records[0].Id);
        output.WriteLine($"Filters={useFilters}: cold={firstReads} payloads/{firstBytes} UTF-16 bytes; warm=0; append=1; pin=1.");
    }

    /// <summary>外部提交使正文重新校验；相同正文保留对象，后续刷新再次免读。</summary>
    [Fact]
    public async Task ExternalCommitRevalidatesTheSnapshotAndThenReturnsToWarmReads()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(0), new());
        await store.AppendAsync(Message(1), new());
        var original = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null);
        using var external = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        external.Open();
        using var command = external.CreateCommand();
        command.CommandText = "UPDATE message_records SET payload=json_set(payload,'$.Content','外部修改') WHERE id='message-0';";
        command.ExecuteNonQuery();

        var refreshed = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null);

        Assert.Equal("外部修改", refreshed.Records[0].Content);
        Assert.NotSame(original.Records[0], refreshed.Records[0]);
        Assert.Same(original.Records[1], refreshed.Records[1]);
        var revalidatedReads = store.PayloadRowsRead;
        var warm = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null);
        Assert.Equal(revalidatedReads, store.PayloadRowsRead);
        Assert.Same(refreshed.Records[0], warm.Records[0]);
    }

    /// <summary>加载更多时仅新增的一百条历史需要读取正文。</summary>
    [Fact]
    public async Task ExpandingHistoryReadsOnlyTheAdditionalPage()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendTestMessagesAsync(Enumerable.Range(0, 300)
            .Select(index => Message(index) with { IsTest = true }).ToArray(), new());
        var filter = new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^广告"] });
        var first = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null, danmuRegexFilter: filter);
        var reads = store.PayloadRowsRead;

        var expanded = await store.LoadFeedAsync("room", MessageKind.Danmu, 200, first.Records[0].Id,
            danmuRegexFilter: filter);

        Assert.Equal(200, expanded.Records.Count);
        Assert.Equal(reads + 100, store.PayloadRowsRead);
        Assert.Same(first.Records[0], expanded.Records[100]);
    }

    private static MessageRecord Message(int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu,
            Content = "历史正文" + index, UserName = "viewer",
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }
}
