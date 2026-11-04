using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Testing;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证本地测试消息的持久化、网络隔离和定向删除。</summary>
public sealed class TestMessageServiceTests
{
    /// <summary>四类消息使用真实存储与阅读规则，同时保留测试标记且不创建翻译任务。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task EveryMessageKindPersistsAndNeverQueuesNetworkTranslation(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var room = new RoomTabState { Id = "room", RoomId = "123", Name = "room" };
        var settings = new PanelSettings
        {
            Reading = new() { GiftAutoPinMinimum = 8 },
            Translation = new() { Enabled = true, BaseUrl = "https://translation.invalid/v1", Model = "test", TargetLanguage = "ja-JP" }
        };
        var changed = new List<string>();
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            var service = new TestMessageService(store, () => settings, changed.Add);
            Assert.Equal(3, await service.AddAsync(room, new()
            {
                Kind = kind, UserName = "viewer", Content = "test message", GiftName = "gift",
                UnitPrice = 2, Quantity = 5, GuardLevel = 2, Count = 3, Translation = "manual translation"
            }));
            Assert.Equal(new[] { room.Id }, changed);
            var rows = await store.LoadPageAsync(room.Id, kind, 0, 10);
            Assert.Equal(3, rows.Count);
            Assert.Equal(3, rows.Select(row => row.Id).Distinct().Count());
            Assert.Equal(3, rows.Select(row => row.UserName).Distinct().Count());
            Assert.All(rows, row =>
            {
                Assert.True(row.IsTest);
                Assert.Equal(room.RoomId, row.RoomId);
                Assert.Equal(kind != MessageKind.Danmu, row.IsPriority);
                Assert.Equal(kind is MessageKind.Gift or MessageKind.Guard ? 5 : 1, row.Quantity);
                Assert.Equal(kind is MessageKind.Gift or MessageKind.Guard ? 10 : kind == MessageKind.SuperChat ? 2 : 0, row.TotalPrice);
                if (kind is MessageKind.Danmu or MessageKind.SuperChat)
                {
                    Assert.Equal("manual translation", row.Translation);
                    Assert.Equal(TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation), row.TranslationFingerprint);
                }
            });
            Assert.Empty(await store.GetPendingAsync(10));
            foreach (var row in rows)
            {
                Assert.False(await store.UpsertTranslationJobAsync(row.Id, row.Generation, "new-configuration"));
            }
            Assert.Empty(await store.GetPendingAsync(10));
            Assert.Empty(await store.LoadRecentAsync(room.Id, kind, 10));
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        Assert.All(await reopened.LoadPageAsync(room.Id, kind, 0, 10), row => Assert.True(row.IsTest));
    }

    /// <summary>清理当前房间测试数据保留真实消息、真实翻译任务、代次及其他房间测试数据。</summary>
    [Fact]
    public async Task ClearingTestMessagesPreservesLiveDataAndOtherRooms()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var other = new RoomTabState { Id = "other", RoomId = "456" };
        var changed = new List<string>();
        var service = new TestMessageService(store, () => new(), changed.Add);
        var live = (await store.AppendAsync(new()
        {
            Id = "live", RoomTabId = room.Id, RoomId = room.RoomId, Content = "live message", MessageId = "source-live"
        }, new(), "live-fingerprint")).Record!;
        await store.TogglePinAsync(live.Id);
        var generation = await store.GetGenerationAsync(room.Id);
        await service.AddAsync(room, new() { UserName = "test", Content = "local", Count = 5 });
        await service.AddAsync(other, new() { UserName = "test", Content = "other", Count = 2 });
        Assert.Equal(5, await service.ClearAsync(room));
        Assert.Equal(generation, await store.GetGenerationAsync(room.Id));
        var survivor = Assert.Single(await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 100));
        Assert.Equal(live.Id, survivor.Id);
        Assert.True(survivor.IsPinned);
        Assert.False(survivor.IsTest);
        Assert.Equal("live", Assert.Single(await store.GetPendingAsync(100)).MessageId);
        Assert.Equal(2, (await store.LoadPageAsync(other.Id, MessageKind.Danmu, 0, 100)).Count);
        Assert.Equal(new[] { room.Id, other.Id, room.Id }, changed);
        Assert.Equal(MessageAppendDisposition.Duplicate, (await store.AppendAsync(live with { Id = "duplicate" }, new())).Disposition);
    }

    /// <summary>批量上限 500 有效，超过上限时不写入任何额外消息。</summary>
    [Fact]
    public async Task BatchLimitIsEnforcedBeforeAnyWrite()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var service = new TestMessageService(store, () => new(), _ => { });
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var request = new TestMessageRequest { UserName = "viewer", Content = "batch", Count = 500 };
        Assert.Equal(500, await service.AddAsync(room, request));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(room, request with { Count = 501 }));
        Assert.Equal(500, (await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 1000)).Count);
    }

    /// <summary>批量中途写入失败时整批回滚，不留下部分测试记录。</summary>
    [Fact]
    public async Task FailedBatchRollsBackEveryMessage()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.GetGenerationAsync("room");
        using (var connection = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_test BEFORE INSERT ON message_records WHEN NEW.id='reject' BEGIN SELECT RAISE(ABORT,'fixture failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.AppendTestMessagesAsync(
            new[] { "first", "reject", "last" }.Select(id => new MessageRecord
            {
                Id = id, RoomTabId = "room", IsTest = true, Content = "test"
            }).ToArray(), new()));
        Assert.Empty(await store.LoadPageAsync("room", MessageKind.Danmu, 0, 10));
    }

    /// <summary>普通入库入口即使显式携带测试标记和翻译配置，也不会安排真实请求。</summary>
    [Fact]
    public async Task NormalAppendAlsoRejectsTranslationJobsForTestMessages()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var result = await store.AppendAsync(new()
        {
            RoomTabId = "room", Content = "test", IsTest = true, Kind = MessageKind.SuperChat
        }, new(), "fingerprint");
        Assert.NotNull(result.Record);
        Assert.Empty(await store.GetPendingAsync(10));
    }

    /// <summary>版本 1 数据库升级时将旧消息视为真实数据，保留正文、置顶和译文。</summary>
    [Fact]
    public async Task VersionOneDatabaseMigrationPreservesExistingMessageState()
    {
        using var directory = new MessageStoreTestDirectory();
        await using (var original = new SqliteMessageStore(directory.DatabasePath))
        {
            var record = (await original.AppendAsync(new()
            {
                Id = "existing", RoomTabId = "room", Content = "existing content"
            }, new(), "fingerprint")).Record!;
            await original.TogglePinAsync(record.Id);
            await original.ApplyTranslationAsync(record.Id, record.Generation, "fingerprint", "existing translation");
        }
        using (var connection = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_message_test;
                ALTER TABLE message_records DROP COLUMN is_test;
                UPDATE message_records SET payload=json_remove(payload,'$.IsTest');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        await using var upgraded = new SqliteMessageStore(directory.DatabasePath);
        var restored = (await upgraded.GetByIdAsync("existing"))!;
        Assert.False(restored.IsTest);
        Assert.True(restored.IsPinned);
        Assert.Equal("existing content", restored.Content);
        Assert.Equal("existing translation", restored.Translation);
        Assert.Equal(0, await upgraded.ClearTestMessagesAsync("room"));
        Assert.NotNull(await upgraded.GetByIdAsync("existing"));
    }
}
