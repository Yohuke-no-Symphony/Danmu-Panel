using System.Threading.Channels;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Translation;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证翻译积压清理不会丢弃已发出请求的结果。</summary>
public sealed class TranslationBacklogTests
{
    /// <summary>高频入队后释放一个并发槽，其他在途请求仍可保存并通知译文。</summary>
    [Theory]
    [InlineData(TranslationBacklogMode.Realtime)]
    [InlineData(TranslationBacklogMode.AllMessages)]
    public async Task OverflowKeepsInFlightTranslations(TranslationBacklogMode mode)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await using var cache = new SqliteTranslationCache(":memory:");
        var settings = new TranslationSettings { Enabled = true, Model = "test", BacklogMode = mode };
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var initial = Enumerable.Range(0, 4).Select(index => Message($"initial-{index}")).ToArray();
        var responses = initial.ToDictionary(message => message.Content,
            _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        var started = Channel.CreateUnbounded<string>();
        var completed = Channel.CreateUnbounded<TranslationCompletedEventArgs>();
        await using var service = new TranslationService(new DelegateTranslationClient(async (request, token) =>
        {
            started.Writer.TryWrite(request.Text);
            if (responses.TryGetValue(request.Text, out var response))
            {
                return await response.Task.WaitAsync(token);
            }

            await Task.Delay(Timeout.Infinite, token);
            return request.Text;
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, args) => completed.Writer.TryWrite(args);
        scheduler.Error += error => completed.Writer.TryComplete(new InvalidOperationException(error));
        foreach (var message in initial)
        {
            await store.AppendAsync(message, new(), fingerprint);
        }

        await scheduler.StartAsync(settings);
        for (var index = 0; index < initial.Length; index++)
        {
            Assert.StartsWith("initial-", await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        for (var index = 0; index < 201; index++)
        {
            await store.AppendAsync(Message($"new-{index}"), new(), fingerprint);
            scheduler.NotifyWorkAvailable();
        }

        responses[initial[0].Content].SetResult("译文-0");
        var first = await completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(initial[0].Id, first.MessageId);
        Assert.StartsWith("new-", await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        var pending = await store.GetPendingAsync(300);
        Assert.Equal(mode == TranslationBacklogMode.Realtime ? 203 : 204, pending.Count);
        foreach (var message in initial.Skip(1))
        {
            Assert.Contains(pending, job => job.MessageId == message.Id);
            responses[message.Content].SetResult("译文-" + message.Content);
        }

        var remaining = initial.Skip(1).Select(message => message.Id).ToHashSet();
        while (remaining.Count > 0)
        {
            var result = await completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(remaining.Remove(result.MessageId));
            Assert.Equal(result.Translation, (await store.GetByIdAsync(result.MessageId))!.Translation);
        }
    }

    private static MessageRecord Message(string text)
    {
        return new() { RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu, Content = text };
    }
}
