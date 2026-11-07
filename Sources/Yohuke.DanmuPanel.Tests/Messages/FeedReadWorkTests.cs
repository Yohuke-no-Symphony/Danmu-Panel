using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>约束消息窗口的筛选工作量、历史边界和运行中的取消行为。</summary>
public sealed class FeedReadWorkTests(ITestOutputHelper output)
{
    /// <summary>优先消息只筛选一次，同时提供完整列表与未读统计。</summary>
    [Fact]
    public async Task PriorityMessagesAreEvaluatedOnceForWindowAndUnreadCount()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendTestMessagesAsync(Enumerable.Range(0, 500).Select(index => Message(index) with
        {
            Kind = MessageKind.Gift, TotalPrice = 10, UnitPrice = 10
        }).ToArray(), new() { GiftAutoPinMinimum = 10 });

        var before = store.FeedVisibilityEvaluations;
        var snapshot = await store.LoadFeedAsync("room", MessageKind.Gift, 100, null);
        var evaluations = store.FeedVisibilityEvaluations - before;

        Assert.Equal(500, snapshot.Records.Count);
        Assert.Equal(500, snapshot.UnreadCount);
        Assert.All(snapshot.Records, record => Assert.True(record.IsPriority));
        Assert.Equal(500, evaluations);
        output.WriteLine($"500 priority gifts: {evaluations} visibility evaluations.");
    }

    /// <summary>小页只筛选页内记录；保留历史边界时不先计数再重复筛选相同记录。</summary>
    [Fact]
    public async Task RetainedHistoryExtendsThePageWithoutRepeatingVisibilityWork()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        foreach (var batch in Enumerable.Range(0, 600).Select(Message).Chunk(500))
        {
            await store.AppendTestMessagesAsync(batch, new());
        }
        var filter = new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^广告"] });
        var before = store.FeedVisibilityEvaluations;
        var first = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null, danmuRegexFilter: filter);
        Assert.Equal(100, store.FeedVisibilityEvaluations - before);
        await store.AppendTestMessagesAsync(Enumerable.Range(600, 8).Select(Message).ToArray(), new());

        before = store.FeedVisibilityEvaluations;
        var retained = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, first.Records[0].Id,
            danmuRegexFilter: filter);
        var evaluations = store.FeedVisibilityEvaluations - before;

        Assert.Equal(108, retained.Records.Count);
        Assert.Equal(first.Records[0].Id, retained.Records[0].Id);
        Assert.Equal(108, evaluations);
        output.WriteLine($"108 retained messages: {evaluations} visibility evaluations.");
    }

    /// <summary>在正则筛选已开始后取消，读取返回取消状态并交还 worker 给后续提交。</summary>
    [Fact]
    public async Task CancellationDuringFilteringCancelsTheTaskAndReleasesTheWorker()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendTestMessagesAsync([Message(0) with { Content = new string('a', 50000) + "!" }], new());
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filter = new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^(a+)+$"] });
        filter.PatternTimedOut += _ =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };
        var reading = store.LoadFeedAsync("room", MessageKind.Danmu, 100, null, cancellation.Token,
            danmuRegexFilter: filter);
        Task<MessageAppendResult> appended;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            appended = store.AppendAsync(Message(1) with { IsTest = false }, new());
            cancellation.Cancel();
        }
        finally
        {
            release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(reading.IsCanceled);
        Assert.NotNull((await appended.WaitAsync(TimeSpan.FromSeconds(5))).Record);
        var restored = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null, danmuRegexFilter: filter);
        Assert.Equal(2, restored.Records.Count);
    }

    private static MessageRecord Message(int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu,
            UserName = "viewer", Content = "历史正文" + index, IsTest = true,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }
}
