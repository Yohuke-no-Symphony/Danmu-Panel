using System.Collections.Concurrent;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证混合逐条规则的模型请求边界和独立恢复。</summary>
public sealed class PerRuleTranslationBlockingTests
{
    /// <summary>全屏蔽和翻译屏蔽分别跳过，单独解除一条规则不会恢复其他屏蔽任务。</summary>
    [Fact]
    public async Task ChangingOneRuleRestoresOnlyItsPreviouslySkippedJobs()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var translationSettings = new TranslationSettings
        {
            Enabled = true, Model = "test", BacklogMode = TranslationBacklogMode.AllMessages
        };
        var ruleSettings = new DanmuRegexFilterSettings
        {
            Rules =
            [
                new() { Pattern = "^full", BlockDisplay = true },
                new() { Pattern = "♪", BlockTranslation = true },
                new() { Pattern = "^disabled" }
            ]
        };
        var hidden = Message("full match");
        var lyrics = Message("lyrics♪");
        var disabled = Message("disabled match");
        var ordinary = Message("ordinary");
        var superChat = Message("full paid match♪") with { Kind = MessageKind.SuperChat };
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(translationSettings);
        await store.AppendBatchAsync([hidden, lyrics, disabled, ordinary, superChat], new(), fingerprint);
        var requests = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var initialCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialSkipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lyricCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hiddenCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedCount = 0;
        var skippedCount = 0;
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requests.Enqueue(request.Text);
            return Task.FromResult("translated " + request.Text);
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Error += errors.Enqueue;
        scheduler.Completed += (_, args) =>
        {
            if (args.MessageId == lyrics.Id)
            {
                lyricCompleted.TrySetResult();
            }
            else if (args.MessageId == hidden.Id)
            {
                hiddenCompleted.TrySetResult();
            }
            else if (Interlocked.Increment(ref completedCount) == 3)
            {
                initialCompleted.TrySetResult();
            }
        };
        scheduler.PendingChanged += (room, _) =>
        {
            if (room.Length > 0 && Interlocked.Increment(ref skippedCount) == 2)
            {
                initialSkipped.TrySetResult();
            }
        };

        await scheduler.StartAsync(translationSettings, danmuFilter: new(ruleSettings));
        await Task.WhenAll(initialCompleted.Task, initialSkipped.Task).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, requests.Count);
        Assert.DoesNotContain(hidden.Content, requests);
        Assert.DoesNotContain("lyrics", requests);
        Assert.Contains(disabled.Content, requests);
        Assert.Contains("full paid match", requests);
        ruleSettings.Rules[1].BlockTranslation = false;
        await scheduler.UpdateSettingsAsync(translationSettings, danmuFilter: new(ruleSettings));
        await lyricCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("lyrics", requests);
        Assert.DoesNotContain(hidden.Content, requests);
        Assert.Null((await store.GetByIdAsync(hidden.Id))!.Translation);
        Assert.False(hiddenCompleted.Task.IsCompleted);
        ruleSettings.Rules[0].BlockDisplay = false;
        await scheduler.UpdateSettingsAsync(translationSettings, danmuFilter: new(ruleSettings));
        await hiddenCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, requests.Count);
        Assert.Equal(fingerprint, (await store.GetByIdAsync(hidden.Id))!.TranslationFingerprint);
        Assert.Empty(await store.GetPendingAsync(10));
        Assert.Empty(errors);
    }

    private static MessageRecord Message(string content)
    {
        return new() { RoomTabId = "tab", Content = content };
    }
}
