using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Testing;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证测试消息的持久化、自动翻译入队和定向删除。</summary>
public sealed class TestMessageServiceTests
{
    /// <summary>四类消息使用真实存储与阅读规则，弹幕和 SC 仅在自动翻译开启时入队并通知调度器。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task EveryMessageKindUsesTheGlobalAutomaticTranslationSetting(MessageKind kind, bool enabled)
    {
        using var directory = new MessageStoreTestDirectory();
        var room = new RoomTabState { Id = "room", RoomId = "123", Name = "room" };
        var settings = new PanelSettings
        {
            Reading = new() { GiftAutoPinMinimum = 8 },
            Translation = new() { Enabled = enabled, BaseUrl = "https://translation.invalid/v1", Model = "test", TargetLanguage = "ja-JP" }
        };
        var changed = new List<string>();
        var notifications = 0;
        var shouldTranslate = enabled && kind is MessageKind.Danmu or MessageKind.SuperChat;
        var supportsMedal = kind == MessageKind.Danmu;
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            var service = new TestMessageService(store, () => settings, changed.Add, () => notifications++);
            Assert.Equal(3, await service.AddAsync(room, new()
            {
                Kind = kind, UserName = "viewer", Content = "test message", GiftName = "gift",
                UnitPrice = 2, Quantity = 5, GuardLevel = 2, Count = 3,
                MedalName = " 测试粉丝牌 ", MedalLevel = 28, MedalGuardLevel = 2,
                RoomGuardLevel = 1, IsRoomAdmin = true
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
                Assert.Equal(supportsMedal ? "测试粉丝牌" : "", row.MedalName);
                Assert.Equal(supportsMedal ? 28 : 0, row.MedalLevel);
                Assert.Equal(supportsMedal ? 2 : 0, row.MedalGuardLevel);
                Assert.Equal(supportsMedal ? 1 : 0, row.RoomGuardLevel);
                Assert.Equal(supportsMedal, row.IsRoomAdmin);
                Assert.Equal(kind != MessageKind.Danmu, row.IsPriority);
                Assert.Equal(kind is MessageKind.Gift or MessageKind.Guard ? 5 : 1, row.Quantity);
                Assert.Equal(kind is MessageKind.Gift or MessageKind.Guard ? 10 : kind == MessageKind.SuperChat ? 2 : 0, row.TotalPrice);
                Assert.Null(row.Translation);
                if (shouldTranslate)
                {
                    Assert.Equal(TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation), row.TranslationFingerprint);
                }
                else
                {
                    Assert.Null(row.TranslationFingerprint);
                }
            });
            var pending = await store.GetPendingAsync(10);
            Assert.Equal(shouldTranslate ? 3 : 0, pending.Count);
            Assert.Equal(shouldTranslate ? 1 : 0, notifications);
            Assert.All(pending, job =>
            {
                Assert.Equal("test message", job.Text);
                Assert.Equal(kind == MessageKind.SuperChat ? 1 : 0, job.Priority);
                Assert.Equal(TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation), job.ConfigurationFingerprint);
            });
            Assert.Empty(await store.LoadRecentAsync(room.Id, kind, 10));
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        Assert.All(await reopened.LoadPageAsync(room.Id, kind, 0, 10), row =>
        {
            Assert.True(row.IsTest);
            Assert.Equal(supportsMedal ? "测试粉丝牌" : "", row.MedalName);
            Assert.Equal(supportsMedal ? 28 : 0, row.MedalLevel);
            Assert.Equal(supportsMedal ? 2 : 0, row.MedalGuardLevel);
            Assert.Equal(supportsMedal ? 1 : 0, row.RoomGuardLevel);
            Assert.Equal(supportsMedal, row.IsRoomAdmin);
        });
        Assert.Equal(shouldTranslate ? 3 : 0, (await reopened.GetPendingAsync(10)).Count);
    }

    /// <summary>空白名称或零等级生成无粉丝牌消息。</summary>
    [Theory]
    [InlineData("", 28)]
    [InlineData("   ", 28)]
    [InlineData("粉丝牌", 0)]
    public async Task EmptyNameOrZeroLevelCreatesMessageWithoutMedal(string name, int level)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var service = new TestMessageService(store, () => new(), _ => { }, () => { });
        var room = new RoomTabState { Id = "room", RoomId = "123" };

        await service.AddAsync(room, new()
        {
            UserName = "viewer", Content = "message", MedalName = name, MedalLevel = level
        });

        var record = Assert.Single(await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 10));
        Assert.Empty(record.MedalName);
        Assert.Equal(0, record.MedalLevel);
    }

    /// <summary>负数粉丝牌等级在写入消息前被拒绝。</summary>
    [Fact]
    public async Task NegativeMedalLevelDoesNotWriteMessages()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var service = new TestMessageService(store, () => new(), _ => { }, () => { });
        var room = new RoomTabState { Id = "room", RoomId = "123" };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(room, new()
        {
            UserName = "viewer", Content = "message", MedalName = "粉丝牌", MedalLevel = -1
        }));

        Assert.Empty(await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 10));
    }

    /// <summary>无效舰队身份在写入前被拒绝。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task InvalidMedalGuardLevelDoesNotWriteMessages(int level)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var service = new TestMessageService(store, () => new(), _ => { }, () => { });
        var room = new RoomTabState { Id = "room", RoomId = "123" };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(room, new()
        {
            UserName = "viewer", Content = "message", MedalGuardLevel = level
        }));

        Assert.Empty(await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 10));
    }

    /// <summary>无效的本房间舰队身份在任何消息写入前被拒绝。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task InvalidRoomGuardLevelDoesNotWriteMessages(int level)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var service = new TestMessageService(store, () => new(), _ => { }, () => { });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(new() { Id = "room" }, new()
        {
            UserName = "viewer", Content = "message", RoomGuardLevel = level
        }));

        Assert.Empty(await store.LoadPageAsync("room", MessageKind.Danmu, 0, 10));
    }

    /// <summary>人工译文不重复请求模型；修改翻译配置后仍可通过普通入口重新排队。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    public async Task ManualTranslationsRemainCompleteAndCanBeRequeuedWithNewSettings(MessageKind kind, bool enabled)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = new PanelSettings { Translation = new() { Enabled = enabled, Model = "test", TargetLanguage = "ja-JP" } };
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var notifications = 0;
        var service = new TestMessageService(store, () => settings, _ => { }, () => notifications++);
        await service.AddAsync(room, new()
        {
            Kind = kind, UserName = "viewer", Content = "source", Translation = "manual translation"
        });
        var record = Assert.Single(await store.LoadPageAsync(room.Id, kind, 0, 10));
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        Assert.Equal("manual translation", record.Translation);
        Assert.Equal(fingerprint, record.TranslationFingerprint);
        Assert.Equal(0, notifications);
        Assert.Empty(await store.GetPendingAsync(10));
        Assert.False(await store.UpsertTranslationJobAsync(record.Id, record.Generation, fingerprint));
        Assert.True(await store.UpsertTranslationJobAsync(record.Id, record.Generation, "new-configuration"));
        var pending = Assert.Single(await store.GetPendingAsync(10));
        Assert.Equal("new-configuration", pending.ConfigurationFingerprint);
        Assert.True(await store.CompleteAsync(pending, "new translation"));
        Assert.Equal("new translation", (await store.GetByIdAsync(record.Id))!.Translation);
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
        var settings = new PanelSettings { Translation = new() { Enabled = true, Model = "test" } };
        var service = new TestMessageService(store, () => settings, changed.Add, () => { });
        var live = (await store.AppendAsync(new()
        {
            Id = "live", RoomTabId = room.Id, RoomId = room.RoomId, Content = "live message", MessageId = "source-live"
        }, new(), "live-fingerprint")).Record!;
        await store.TogglePinAsync(live.Id);
        var generation = await store.GetGenerationAsync(room.Id);
        await service.AddAsync(room, new() { UserName = "test", Content = "local", Count = 5 });
        await service.AddAsync(other, new() { UserName = "test", Content = "other", Count = 2 });
        var beforeClear = await store.GetPendingAsync(100);
        Assert.Equal(8, beforeClear.Count);
        var removedJob = beforeClear.First(job => job.RoomTabId == room.Id && job.MessageId != live.Id);
        Assert.Equal(5, await service.ClearAsync(room));
        Assert.Equal(generation, await store.GetGenerationAsync(room.Id));
        var survivor = Assert.Single(await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 100));
        Assert.Equal(live.Id, survivor.Id);
        Assert.True(survivor.IsPinned);
        Assert.False(survivor.IsTest);
        var pending = await store.GetPendingAsync(100);
        Assert.Equal(3, pending.Count);
        Assert.Contains(pending, job => job.MessageId == "live");
        Assert.Equal(2, pending.Count(job => job.RoomTabId == other.Id));
        Assert.False(await store.CompleteAsync(removedJob, "late translation"));
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
        var service = new TestMessageService(store, () => new(), _ => { }, () => { });
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var request = new TestMessageRequest { UserName = "viewer", Content = "batch", Count = 500 };
        Assert.Equal(500, await service.AddAsync(room, request));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddAsync(room, request with { Count = 501 }));
        Assert.Equal(500, (await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 1000)).Count);
    }

    /// <summary>批量中途写入失败时消息和已创建的翻译任务一并回滚。</summary>
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
            }).ToArray(), new(), "fingerprint"));
        Assert.Empty(await store.LoadPageAsync("room", MessageKind.Danmu, 0, 10));
        Assert.Empty(await store.GetPendingAsync(10));
    }

    /// <summary>普通入库入口携带测试标记时仍创建标准 SC 翻译任务。</summary>
    [Fact]
    public async Task NormalAppendUsesStandardTranslationJobsForTestMessages()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var result = await store.AppendAsync(new()
        {
            RoomTabId = "room", Content = "test", IsTest = true, Kind = MessageKind.SuperChat
        }, new(), "fingerprint");
        Assert.NotNull(result.Record);
        var pending = Assert.Single(await store.GetPendingAsync(10));
        Assert.Equal(result.Record.Id, pending.MessageId);
        Assert.Equal("fingerprint", pending.ConfigurationFingerprint);
        Assert.Equal(1, pending.Priority);
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
