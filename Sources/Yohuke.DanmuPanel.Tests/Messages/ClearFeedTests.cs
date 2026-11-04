using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证按面板清空的持久化范围、翻译回调和补拉屏障。</summary>
public sealed class ClearFeedTests
{
    /// <summary>清空覆盖全部分页和阅读状态，只移除当前房间的指定类型及其翻译任务。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ClearOnlyRemovesTargetFeedAndPersistsAcrossRestart(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var targetIds = new HashSet<string>();
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            foreach (var other in Enum.GetValues<MessageKind>())
            {
                await store.AppendAsync(Message(other) with { Id = "other-" + other }, new(), "config");
                await store.AppendAsync(Message(other) with { RoomTabId = "other-room" }, new(), "config");
                if (other == kind)
                {
                    targetIds.Add("other-" + other);
                }
            }
            for (var index = 0; index < 105; index++)
            {
                var record = Message(kind) with { IsTest = index % 2 == 0 };
                targetIds.Add(record.Id);
                await store.AppendAsync(record, new(), "config");
                if (kind == MessageKind.Danmu)
                {
                    await store.TogglePinAsync(record.Id);
                }
                else
                {
                    await store.MarkReadAsync(record.Id, record.Quantity, record.TotalPrice);
                }
            }
            var jobs = await store.GetPendingAsync(500);

            await store.ClearFeedAsync("tab", kind);

            Assert.Empty(await store.LoadPageAsync("tab", kind, 0, 500));
            Assert.Equal(0, await store.GetGenerationAsync("tab"));
            Assert.Equal(jobs.Where(job => !targetIds.Contains(job.MessageId)).Select(job => job.Id).Order(),
                (await store.GetPendingAsync(500)).Select(job => job.Id).Order());
            foreach (var job in jobs.Where(job => targetIds.Contains(job.MessageId)))
            {
                Assert.False(await store.CompleteAsync(job, "late translation"));
                Assert.False(await store.ApplyTranslationAsync(job.MessageId, job.Generation, "config", "late translation"));
            }
            foreach (var job in jobs.Where(job => !targetIds.Contains(job.MessageId)))
            {
                Assert.True(await store.CompleteAsync(job, "retained translation"));
            }
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        foreach (var other in Enum.GetValues<MessageKind>())
        {
            var records = await reopened.LoadPageAsync("tab", other, 0, 500);
            if (other == kind)
            {
                Assert.Empty(records);
            }
            else
            {
                Assert.Equal("other-" + other, Assert.Single(records).Id);
            }
            Assert.Single(await reopened.LoadPageAsync("other-room", other, 0, 500));
        }
    }

    /// <summary>清空屏障跨重启保留，阻止旧历史回填但继续接受新消息和其他面板历史。</summary>
    [Fact]
    public async Task ClearBlocksOnlyTargetHistoryAndAcceptsNewMessages()
    {
        using var directory = new MessageStoreTestDirectory();
        var old = Message(MessageKind.Danmu) with { IsHistory = true, Time = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            await store.ClearFeedAsync("tab", MessageKind.Danmu);
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        Assert.Equal(MessageAppendDisposition.ClearedHistory, (await reopened.AppendAsync(old, new())).Disposition);
        Assert.Equal(MessageAppendDisposition.ClearedHistory, (await reopened.AppendAsync(old with
        {
            HasSourceTimestamp = false, Time = DateTimeOffset.UtcNow.AddMinutes(1)
        }, new())).Disposition);
        Assert.Equal(MessageAppendDisposition.Appended, (await reopened.AppendAsync(old with
        {
            Kind = MessageKind.SuperChat
        }, new())).Disposition);
        Assert.Equal(MessageAppendDisposition.Appended, (await reopened.AppendAsync(Message(MessageKind.Danmu), new())).Disposition);
        Assert.Equal(MessageAppendDisposition.Appended, (await reopened.AppendAsync(old with
        {
            Id = Guid.NewGuid().ToString("N"), Time = DateTimeOffset.UtcNow.AddMinutes(1)
        }, new())).Disposition);
    }

    /// <summary>礼物清空保存连击基线，后续累计包仅显示清空后的新增数量和价值。</summary>
    [Fact]
    public async Task ClearingGiftsPreservesComboBaselineAndDeduplication()
    {
        using var directory = new MessageStoreTestDirectory();
        var gift = Message(MessageKind.Gift) with
        {
            MessageId = "first", ComboId = "combo", GiftId = "gift", BatchComboCount = 3, UnitPrice = 10
        };
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            await store.AppendAsync(gift, new());
            await store.ClearFeedAsync("tab", MessageKind.Danmu);
            Assert.Equal(3, Assert.Single(await store.LoadPageAsync("tab", MessageKind.Gift, 0, 10)).Quantity);
            await store.ClearFeedAsync("tab", MessageKind.Gift);
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        Assert.Equal(MessageAppendDisposition.Duplicate, (await reopened.AppendAsync(gift, new())).Disposition);
        var next = await reopened.AppendAsync(gift with
        {
            Id = Guid.NewGuid().ToString("N"), MessageId = "next", BatchComboCount = 5
        }, new());
        Assert.Equal(2, next.Record!.Quantity);
        Assert.Equal(20, next.Record.TotalPrice);
        Assert.False(next.Record.IsRead);
    }

    /// <summary>版本 2 数据库可无损升级，并获得按面板清空的能力。</summary>
    [Fact]
    public async Task VersionTwoMigrationPreservesMessagesAndTranslations()
    {
        using var directory = new MessageStoreTestDirectory();
        var record = Message(MessageKind.Danmu);
        await using (var original = new SqliteMessageStore(directory.DatabasePath))
        {
            await original.AppendAsync(record, new(), "config");
            await original.TogglePinAsync(record.Id);
            await original.ApplyTranslationAsync(record.Id, 0, "config", "translation");
        }
        using (var connection = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS message_feed_clears; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        await using var upgraded = new SqliteMessageStore(directory.DatabasePath);
        var restored = (await upgraded.GetByIdAsync(record.Id))!;
        Assert.True(restored.IsPinned);
        Assert.Equal("translation", restored.Translation);
        await upgraded.ClearFeedAsync("tab", MessageKind.Danmu);
        Assert.Null(await upgraded.GetByIdAsync(record.Id));
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new() { RoomTabId = "tab", RoomId = "123", Kind = kind, Content = "正文", TotalPrice = 30 };
    }
}
