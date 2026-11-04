using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证持久消息、连击和清空屏障。</summary>
public sealed class SqliteMessageStoreTests
{
    /// <summary>提交后无需正常退出，另一连接已经可以恢复消息、置顶和译文。</summary>
    [Fact]
    public async Task CommittedRecordsAreRecoverableBeforeWriterDisposal()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var writer = new SqliteMessageStore(directory.DatabasePath);
        var message = Message(MessageKind.Danmu);
        await writer.AppendAsync(message, new(), "configuration");
        await writer.TogglePinAsync(message.Id);
        Assert.True(await writer.ApplyTranslationAsync(message.Id, 0, "configuration", "hello"));

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var recovered = Assert.Single(await reopened.LoadPageAsync("tab", MessageKind.Danmu, 0, 20));
        Assert.True(recovered.IsPinned);
        Assert.Equal("hello", recovered.Translation);
        Assert.Equal("configuration", recovered.TranslationFingerprint);
        Assert.Empty(await reopened.GetPendingAsync(20));
    }

    /// <summary>不把普通弹幕同文当作重复消息，稳定事件 ID 则跨重启去重。</summary>
    [Fact]
    public async Task StableEventIdsDeduplicateButEqualDanmuTextDoesNot()
    {
        using var directory = new MessageStoreTestDirectory();
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            await store.AppendAsync(Message(MessageKind.Danmu), new());
            await store.AppendAsync(Message(MessageKind.Danmu), new());
            await store.AppendAsync(Gift("gift-event", 0, 3), new());
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var duplicate = await reopened.AppendAsync(Gift("gift-event", 0, 3), new());
        Assert.Equal(MessageAppendDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(2, (await reopened.LoadPageAsync("tab", MessageKind.Danmu, 0, 20)).Count);
        Assert.Equal(3, Assert.Single(await reopened.LoadPageAsync("tab", MessageKind.Gift, 0, 20)).Quantity);
    }

    /// <summary>连击的已读确认数量和价值跨重启恢复。</summary>
    [Theory]
    [InlineData(GiftReadResetPolicy.ConfirmOnce, true)]
    [InlineData(GiftReadResetPolicy.NewQuantity, false)]
    [InlineData(GiftReadResetPolicy.NewValueThreshold, true)]
    public async Task ComboReadPolicySurvivesRestart(GiftReadResetPolicy policy, bool expectedRead)
    {
        using var directory = new MessageStoreTestDirectory();
        var options = new MessageReadOptions { GiftAutoPinMinimum = 20, GiftReadResetPolicy = policy };
        string id;
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            var first = await store.AppendAsync(Gift("one", 2), options);
            id = first.Record!.Id;
            await store.MarkReadAsync(id, 2, 20);
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var update = await reopened.AppendAsync(Gift("two", 3), options);
        Assert.Equal(MessageAppendDisposition.Updated, update.Disposition);
        Assert.Equal(id, update.Record!.Id);
        Assert.Equal(3, update.Record.Quantity);
        Assert.Equal(30, update.Record.TotalPrice);
        Assert.Equal(expectedRead, update.Record.IsRead);
        if (policy == GiftReadResetPolicy.NewValueThreshold)
        {
            var threshold = await reopened.AppendAsync(Gift("three", 4), options);
            Assert.False(threshold.Record!.IsRead);
            Assert.True(threshold.Record.IsPriority);
        }
    }

    /// <summary>乱序累计数不回退，重复增量包不会累加第二次。</summary>
    [Fact]
    public async Task ComboCumulativeAndIncrementalEventsAreIdempotent()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Gift("first", 4), new());
        Assert.Equal(MessageAppendDisposition.Duplicate, (await store.AppendAsync(Gift("old", 2), new())).Disposition);
        await store.AppendAsync(Gift("increment", 0, 2), new());
        Assert.Equal(MessageAppendDisposition.Duplicate, (await store.AppendAsync(Gift("increment", 0, 2), new())).Disposition);
        var record = Assert.Single(await store.LoadPageAsync("tab", MessageKind.Gift, 0, 20));
        Assert.Equal(6, record.Quantity);
        Assert.Equal(60, record.TotalPrice);
    }

    /// <summary>点击确认旧画面时，不确认同时到达的新数量。</summary>
    [Fact]
    public async Task ReadAcknowledgesObservedQuantityOnly()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftReadResetPolicy = GiftReadResetPolicy.NewQuantity };
        var first = (await store.AppendAsync(Gift("first", 1), options)).Record!;
        await store.AppendAsync(Gift("second", 2), options);
        var read = await store.MarkReadAsync(first.Id, first.Quantity, first.TotalPrice);
        Assert.False(read!.IsRead);
        Assert.Equal(1, read.ReadQuantity);
    }

    /// <summary>清空同时取消旧消息、翻译任务和历史补拉，并保存连击增量基线。</summary>
    [Fact]
    public async Task ClearRejectsStaleCallbacksAndDoesNotResurrectComboTotals()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message(MessageKind.Danmu);
        await store.AppendAsync(message, new(), "config");
        var job = Assert.Single(await store.GetPendingAsync(20));
        await store.AppendAsync(Gift("gift-first", 3), new());
        await store.ClearRoomAsync("tab");

        Assert.Equal(1, await store.GetGenerationAsync("tab"));
        Assert.False(await store.CompleteAsync(job, "late result"));
        Assert.False(await store.ApplyTranslationAsync(message.Id, 0, "config", "late result"));
        Assert.Equal(MessageAppendDisposition.Stale, (await store.AppendAsync(Message(MessageKind.Danmu), new())).Disposition);
        var history = Message(MessageKind.Danmu) with { Generation = 1, IsHistory = true, Time = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.Equal(MessageAppendDisposition.ClearedHistory, (await store.AppendAsync(history, new())).Disposition);
        Assert.Equal(MessageAppendDisposition.ClearedHistory, (await store.AppendAsync(history with
        {
            Id = Guid.NewGuid().ToString("N"), Time = DateTimeOffset.UtcNow.AddMinutes(1), HasSourceTimestamp = false
        }, new())).Disposition);

        var next = await store.AppendAsync(Gift("gift-next", 5) with { Generation = 1 }, new());
        Assert.Equal(2, next.Record!.Quantity);
        Assert.Equal(20, next.Record.TotalPrice);
        Assert.Empty(await store.LoadPageAsync("tab", MessageKind.Danmu, 0, 20));
    }

    /// <summary>全局清空使已经开始监听但尚无消息的房间代次失效。</summary>
    [Fact]
    public async Task ClearAllInvalidatesRegisteredEmptyRoom()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        Assert.Equal(0, await store.GetGenerationAsync("tab"));
        await store.ClearAllAsync();
        Assert.Equal(MessageAppendDisposition.Stale, (await store.AppendAsync(Message(MessageKind.Danmu), new())).Disposition);
    }

    /// <summary>置顶与未读优先，取消后恢复原消息时间顺序。</summary>
    [Fact]
    public async Task PinAndReadOrderingIsPersisted()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var older = Message(MessageKind.Danmu) with { Time = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newer = Message(MessageKind.Danmu);
        await store.AppendAsync(older, new());
        await store.AppendAsync(newer, new());
        await store.TogglePinAsync(older.Id);
        Assert.Equal(older.Id, (await store.LoadPageAsync("tab", MessageKind.Danmu, 0, 1))[0].Id);
        await store.TogglePinAsync(older.Id);
        Assert.Equal(newer.Id, (await store.LoadPageAsync("tab", MessageKind.Danmu, 0, 1))[0].Id);

        var oldSc = Message(MessageKind.SuperChat) with { Time = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var newSc = Message(MessageKind.SuperChat);
        await store.AppendAsync(oldSc, new());
        await store.AppendAsync(newSc, new());
        await store.MarkReadAsync(newSc.Id, 1, 0);
        Assert.Equal(oldSc.Id, (await store.LoadPageAsync("tab", MessageKind.SuperChat, 0, 1))[0].Id);
    }

    /// <summary>配置更新后，旧翻译结果失效，重排不修改消息阅读状态。</summary>
    [Fact]
    public async Task TranslationConfigurationReplacementRejectsOldCompletion()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message(MessageKind.Danmu);
        await store.AppendAsync(message, new(), "old");
        var oldJob = Assert.Single(await store.GetPendingAsync(20));
        await store.ResetPendingAsync("new");
        Assert.False(await store.CompleteAsync(oldJob, "wrong"));
        var newJob = Assert.Single(await store.GetPendingAsync(20));
        Assert.True(await store.CompleteAsync(newJob, "right"));
        Assert.Equal("right", (await store.GetByIdAsync(message.Id))!.Translation);
        Assert.False(await store.UpsertTranslationJobAsync(message.Id, 0, "new"));
    }

    /// <summary>超时和数量清理均保留在途任务、SC 与置顶消息，在途任务完成后仍可保存译文。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TranslationTrimmingProtectsInFlightAndImportantJobs(bool expireWaiting)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var inFlight = Message(MessageKind.Danmu);
        var waiting = new[] { Message(MessageKind.Danmu), Message(MessageKind.Danmu) };
        var superChat = Message(MessageKind.SuperChat);
        var pinned = Message(MessageKind.Danmu);
        foreach (var message in new[] { inFlight, waiting[0], waiting[1], superChat, pinned })
        {
            await store.AppendAsync(message, new(), "config");
        }
        await store.TogglePinAsync(pinned.Id);
        var jobs = await store.GetPendingAsync(10);
        var cutoff = expireWaiting ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.MinValue;

        var trimmed = await store.TrimPendingAsync(cutoff, 1, [inFlight.Id]);

        Assert.Equal(expireWaiting ? 2 : 1, trimmed);
        var pending = await store.GetPendingAsync(10);
        var expected = new[] { inFlight.Id, superChat.Id, pinned.Id }.ToHashSet();
        if (!expireWaiting)
        {
            var newestWaiting = jobs.Where(job => waiting.Any(message => message.Id == job.Id))
                .OrderByDescending(job => job.CreatedAt.ToUnixTimeMilliseconds())
                .ThenByDescending(job => job.Id, StringComparer.Ordinal).First();
            expected.Add(newestWaiting.Id);
        }
        Assert.True(expected.SetEquals(pending.Select(job => job.Id)));
        Assert.True(await store.CompleteAsync(jobs.Single(job => job.Id == inFlight.Id), "译文"));
        Assert.Equal("译文", (await store.GetByIdAsync(inFlight.Id))!.Translation);
        foreach (var skipped in jobs.Where(job => !expected.Contains(job.Id)))
        {
            Assert.False(await store.CompleteAsync(skipped, "不应写入"));
            Assert.Null((await store.GetByIdAsync(skipped.MessageId))!.Translation);
        }
    }

    /// <summary>没有在途任务时，零等待容量仍跳过全部普通任务。</summary>
    [Fact]
    public async Task TranslationTrimmingWithNoInFlightJobsHonorsZeroCapacity()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(MessageKind.Danmu), new(), "config");

        Assert.Equal(1, await store.TrimPendingAsync(DateTimeOffset.MinValue, 0, []));
        Assert.Empty(await store.GetPendingAsync(10));
    }

    /// <summary>礼物阈值更新只修改优先排序，不重置已读记录。</summary>
    [Fact]
    public async Task ThresholdReevaluationKeepsReadState()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var gift = (await store.AppendAsync(Gift("gift", 2), new() { GiftAutoPinMinimum = 30 })).Record!;
        Assert.False(gift.IsPriority);
        await store.ReevaluateReadOptionsAsync(new() { GiftAutoPinMinimum = 10 });
        Assert.True((await store.GetByIdAsync(gift.Id))!.IsPriority);
        await store.MarkReadAsync(gift.Id, 2, 20);
        await store.ReevaluateReadOptionsAsync(new() { GiftAutoPinMinimum = 0 });
        var read = await store.GetByIdAsync(gift.Id);
        Assert.True(read!.IsRead);
        Assert.False(read.IsPriority);
    }

    /// <summary>数据库写入失败不会返回成功消息或创建孤立翻译任务。</summary>
    [Fact]
    public async Task FailedInsertRollsBackEventAndTranslationJob()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.GetGenerationAsync("tab");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_message BEFORE INSERT ON message_records BEGIN SELECT RAISE(ABORT,'fixture write failure'); END;";
        command.ExecuteNonQuery();
        var message = Message(MessageKind.Danmu) with { MessageId = "stable-id" };
        await Assert.ThrowsAsync<SqliteException>(() => store.AppendAsync(message, new(), "config"));
        Assert.Empty(await store.GetPendingAsync(20));
        command.CommandText = "DROP TRIGGER reject_message;";
        command.ExecuteNonQuery();
        Assert.Equal(MessageAppendDisposition.Appended, (await store.AppendAsync(message, new(), "config")).Disposition);
    }

    /// <summary>同房间的不同工作区标签具有独立的历史和阅读状态。</summary>
    [Fact]
    public async Task RoomTabsRemainIndependent()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var source = Message(MessageKind.SuperChat) with { MessageId = "same-upstream" };
        await store.AppendAsync(source, new());
        await store.AppendAsync(source with { Id = Guid.NewGuid().ToString("N"), RoomTabId = "other" }, new());
        await store.ClearRoomAsync("tab");
        Assert.Single(await store.LoadPageAsync("other", MessageKind.SuperChat, 0, 20));
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new() { RoomTabId = "tab", RoomId = "123", Kind = kind, UserUid = 10, UserName = "viewer", Content = "同样的弹幕" };
    }

    private static MessageRecord Gift(string eventId, int cumulativeCount, int quantity = 1)
    {
        return Message(MessageKind.Gift) with
        {
            MessageId = eventId, GiftId = "gift", GiftName = "礼物", ComboId = "combo",
            BatchComboCount = cumulativeCount, Quantity = quantity, UnitPrice = 10
        };
    }
}
