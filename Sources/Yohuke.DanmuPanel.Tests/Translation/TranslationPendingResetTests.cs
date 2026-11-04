using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证翻译待办只在启用且配置身份变化时改写，并保留旧请求取消屏障。</summary>
public sealed class TranslationPendingResetTests
{
    /// <summary>同指纹重置不会重新插入或更新现有消息和翻译任务。</summary>
    [Theory]
    [InlineData("message_records")]
    [InlineData("translation_jobs")]
    public async Task ResetWithCurrentFingerprintDoesNotWritePendingRecords(string guardedTable)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(Settings(true, "zh-CN"));
        var message = Message();
        await store.AppendAsync(message, new(), fingerprint);
        var original = Assert.Single(await store.GetPendingAsync(10));
        using var guard = RejectWrites(directory, guardedTable);

        await store.ResetPendingAsync(fingerprint);

        var retained = Assert.Single(await store.GetPendingAsync(10));
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal(original.ConfigurationFingerprint, retained.ConfigurationFingerprint);
        Assert.Equal(fingerprint, (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
    }

    /// <summary>以当前配置启动调度时直接恢复待办，不先重写已有消息或任务。</summary>
    [Theory]
    [InlineData("message_records")]
    [InlineData("translation_jobs")]
    public async Task StartupWithCurrentFingerprintDoesNotRewritePendingRecords(string guardedTable)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings(true, "zh-CN");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        await store.AppendAsync(Message(), new(), fingerprint);
        var client = new ControlledTranslationClient();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        using var guard = RejectWrites(directory, guardedTable);

        await scheduler.StartAsync(settings);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(fingerprint, Assert.Single(await store.GetPendingAsync(10)).ConfigurationFingerprint);
        Assert.Equal(1, client.Calls);
    }

    /// <summary>关闭翻译启动及编辑目标语言都保留旧待办，避免触发全库改写。</summary>
    [Fact]
    public async Task DisabledStartupAndConfigurationChangesDoNotRewritePendingRecords()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var originalFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(Settings(true, "zh-CN"));
        var message = Message();
        await store.AppendAsync(message, new(), originalFingerprint);
        var client = new ControlledTranslationClient();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        using var guard = RejectWrites(directory, "message_records", "translation_jobs");

        await scheduler.StartAsync(Settings(false, "ja-JP"));
        await scheduler.UpdateSettingsAsync(Settings(false, "en-US"));

        Assert.Equal(originalFingerprint, Assert.Single(await store.GetPendingAsync(10)).ConfigurationFingerprint);
        Assert.Equal(originalFingerprint, (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
        Assert.Equal(0, client.Calls);
    }

    /// <summary>重新启用时把保留待办切换到当前目标，拒绝旧任务完成后正常写入新译文。</summary>
    [Fact]
    public async Task EnablingAfterDisabledChangesResetsFingerprintAndRejectsOldCompletion()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var originalFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(Settings(true, "zh-CN"));
        var message = Message();
        await store.AppendAsync(message, new(), originalFingerprint);
        var previousJob = Assert.Single(await store.GetPendingAsync(10));
        var client = new ControlledTranslationClient();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();
        await scheduler.StartAsync(Settings(false, "ja-JP"));
        await scheduler.UpdateSettingsAsync(Settings(false, "en-US"));
        Assert.Equal(originalFingerprint, Assert.Single(await store.GetPendingAsync(10)).ConfigurationFingerprint);
        var enabled = Settings(true, "en-US");
        var currentFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(enabled);

        await scheduler.UpdateSettingsAsync(enabled);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var currentJob = Assert.Single(await store.GetPendingAsync(10));
        Assert.Equal(currentFingerprint, currentJob.ConfigurationFingerprint);
        Assert.Equal(previousJob.CreatedAt, currentJob.CreatedAt);
        Assert.False(await store.CompleteAsync(previousJob, "obsolete translation"));
        Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
        client.Response.TrySetResult("current translation");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var translated = (await store.GetByIdAsync(message.Id))!;
        Assert.Equal(currentFingerprint, translated.TranslationFingerprint);
        Assert.Equal("current translation", translated.Translation);
        Assert.Empty(await store.GetPendingAsync(10));
    }

    /// <summary>关闭翻译仍取消在途请求，但不会为了目标变化重写保留的待办。</summary>
    [Fact]
    public async Task DisablingCancelsInFlightRequestWithoutRewritingPendingRecords()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings(true, "zh-CN");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var message = Message();
        await store.AppendAsync(message, new(), fingerprint);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegateTranslationClient(async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return "obsolete translation";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        await scheduler.StartAsync(settings);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var guard = RejectWrites(directory, "message_records", "translation_jobs");

        await scheduler.UpdateSettingsAsync(Settings(false, "ja-JP"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(fingerprint, Assert.Single(await store.GetPendingAsync(10)).ConfigurationFingerprint);
        Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
    }

    private static SqliteConnection RejectWrites(MessageStoreTestDirectory directory, params string[] tables)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        try
        {
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TRIGGER reject_insert_{table} BEFORE INSERT ON {table}
                    BEGIN SELECT RAISE(ABORT,'Unexpected translation reset insert'); END;
                    CREATE TRIGGER reject_update_{table} BEFORE UPDATE ON {table}
                    BEGIN SELECT RAISE(ABORT,'Unexpected translation reset update'); END;
                    CREATE TRIGGER reject_delete_{table} BEFORE DELETE ON {table}
                    BEGIN SELECT RAISE(ABORT,'Unexpected translation reset delete'); END;
                    """;
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static TranslationSettings Settings(bool enabled, string targetLanguage)
    {
        return new()
        {
            Enabled = enabled,
            Model = "test",
            TargetLanguage = targetLanguage,
            BacklogMode = TranslationBacklogMode.AllMessages
        };
    }

    private static MessageRecord Message()
    {
        return new()
        {
            RoomTabId = "translation-reset",
            Kind = MessageKind.Danmu,
            UserName = "viewer",
            Content = "hello"
        };
    }
}
