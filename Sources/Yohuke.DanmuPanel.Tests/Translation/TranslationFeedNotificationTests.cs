using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>翻译任务保留消息类型，避免一次译文变化重读全部消息面板。</summary>
public sealed class TranslationFeedNotificationTests
{
    /// <summary>任务持久化记录消息类型，重启时仍可精确定位需要刷新的面板。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task StoredJobsPreserveTheirMessageKind(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            await store.AppendAsync(new()
            {
                Id = "message", RoomTabId = "room", Kind = kind, Content = "hello"
            }, new(), "config");
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var job = Assert.Single(await reopened.GetPendingAsync(1));

        Assert.Equal(kind, job.Kind);
    }

    /// <summary>完成和失败通知只指向任务所属类型，旧任务缺少类型时保留广播回退。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task JobNotificationsPreserveTheirScope(MessageKind? kind, bool fails)
    {
        var job = JsonSerializer.Deserialize<TranslationJob>("""
            {"Id":"message","MessageId":"message","RoomTabId":"room","Text":"hello"}
            """)!;
        if (kind is not null)
        {
            job = job with { Kind = kind };
        }
        var store = new MemoryTranslationJobStore();
        store.Pending.Add(job);
        var notification = new TaskCompletionSource<(string Room, MessageKind? Kind, bool Completed)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((_, _) => fails
            ? Task.FromException<string>(new TranslationException("translation.failed"))
            : Task.FromResult("translated")), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, args) => notification.TrySetResult((args.RoomTabId, args.Kind, true));
        scheduler.PendingChanged += (room, changedKind) => notification.TrySetResult((room, changedKind, false));

        await scheduler.StartAsync(new()
        {
            Enabled = true, Model = "test", BacklogMode = TranslationBacklogMode.AllMessages
        });

        Assert.Equal(("room", kind, !fails), await notification.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
