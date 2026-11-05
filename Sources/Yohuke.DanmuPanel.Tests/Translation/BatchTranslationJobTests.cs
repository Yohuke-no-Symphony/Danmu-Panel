using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证历史翻译任务批量登记时的现存消息校验及原子性。</summary>
public sealed class BatchTranslationJobTests
{
    /// <summary>按存储中的当前正文创建任务，跳过过时代次、不支持的类型和已完成译文。</summary>
    [Fact]
    public async Task BatchUsesCurrentRecordsAndRejectsIneligibleRequests()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var messages = new[] { Message("ordinary"), Message("sc") with { Kind = MessageKind.SuperChat },
            Message("gift") with { Kind = MessageKind.Gift }, Message("translated"), Message("stale") };
        await store.AppendBatchAsync(messages, new());
        await store.UpsertTranslationJobAsync("translated", 0, "configuration");
        await store.ApplyTranslationAsync("translated", 0, "configuration", "completed");
        var requests = messages.Select(message => message with { Content = "stale caller content" }).ToArray();
        requests[^1] = requests[^1] with { Generation = 1 };

        var queued = await store.UpsertTranslationJobsAsync(requests, "configuration");

        Assert.Equal(new[] { "ordinary", "sc" }, queued.Order().ToArray());
        var jobs = await store.GetPendingAsync(10);
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Equal("当前正文", job.Text));
        Assert.Equal("completed", (await store.GetByIdAsync("translated"))!.Translation);
    }

    /// <summary>批次中途写入失败时，不留下部分任务或部分指纹更新。</summary>
    [Fact]
    public async Task FailedJobBatchRollsBackEarlierFingerprintChanges()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var messages = new[] { Message("first"), Message("last") };
        await store.AppendBatchAsync(messages, new());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_last_job BEFORE INSERT ON translation_jobs WHEN NEW.id='last'
            BEGIN SELECT RAISE(ABORT,'last translation rejected'); END;
            """;
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<SqliteException>(() => store.UpsertTranslationJobsAsync(messages, "configuration"));

        Assert.Empty(await store.GetPendingAsync(10));
        Assert.Null((await store.GetByIdAsync("first"))!.TranslationFingerprint);
        Assert.Null((await store.GetByIdAsync("last"))!.TranslationFingerprint);
    }

    /// <summary>重复登记同代次和指纹的等待任务不重写正文或任务，也不改变任务时间。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPendingJobIsReusedWithoutWriting(bool useSingleApi)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message("pending");
        await store.AppendAsync(message, new(), "configuration");
        var original = Assert.Single(await store.GetPendingAsync(10));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_message BEFORE INSERT ON message_records
            BEGIN SELECT RAISE(ABORT,'pending message rewritten'); END;
            CREATE TRIGGER reject_job BEFORE INSERT ON translation_jobs
            BEGIN SELECT RAISE(ABORT,'pending job rewritten'); END;
            """;
        command.ExecuteNonQuery();

        if (useSingleApi)
        {
            Assert.True(await store.UpsertTranslationJobAsync(message.Id, message.Generation, "configuration"));
        }
        else
        {
            Assert.Contains(message.Id, await store.UpsertTranslationJobsAsync([message], "configuration"));
        }

        var retained = Assert.Single(await store.GetPendingAsync(10));
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal(original.ConfigurationFingerprint, retained.ConfigurationFingerprint);
    }

    /// <summary>滞后的后台批次不能覆盖已切换的配置，显式单条操作仍可以更换配置。</summary>
    [Fact]
    public async Task StaleSnapshotCannotReplaceANewerFingerprint()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message("changed");
        await store.AppendAsync(message, new(), "F0");
        var original = (await store.GetByIdAsync(message.Id))!;
        Assert.True(await store.UpsertTranslationJobAsync(message.Id, message.Generation, "F2"));

        var queued = await store.UpsertTranslationJobsAsync([original], "F1");

        Assert.Empty(queued);
        Assert.Equal("F2", (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
        Assert.Equal("F2", Assert.Single(await store.GetPendingAsync(10)).ConfigurationFingerprint);
        Assert.True(await store.UpsertTranslationJobAsync(message.Id, message.Generation, "F1"));
        Assert.Equal("F1", (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
    }

    /// <summary>失败或裁剪的同指纹任务仍可被重新登记。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTrimmedJobsCanStillBeRetried(bool trim)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message("retry");
        await store.AppendAsync(message, new(), "configuration");
        var original = Assert.Single(await store.GetPendingAsync(10));
        if (trim)
        {
            Assert.Equal(1, await store.TrimPendingAsync(DateTimeOffset.UtcNow.AddMinutes(1), 0, []));
        }
        else
        {
            Assert.True(await store.FailAsync(original, "retry fixture"));
        }

        var queued = await store.UpsertTranslationJobsAsync([message], "configuration");

        Assert.Contains(message.Id, queued);
        Assert.Equal(message.Id, Assert.Single(await store.GetPendingAsync(10)).MessageId);
    }

    private static MessageRecord Message(string id)
    {
        return new() { Id = id, RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu, Content = "当前正文" };
    }
}
