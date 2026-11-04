using System.Collections.Concurrent;
using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class TranslationSchedulerTests
{
    [Theory]
    [InlineData(TranslationBacklogMode.Realtime, 1, 1)]
    [InlineData(TranslationBacklogMode.AllMessages, 2, 0)]
    public async Task RealtimeExpiresOrdinaryMessagesButKeepsImportantMessages(TranslationBacklogMode mode, int completed, int skipped)
    {
        var store = new MemoryTranslationJobStore();
        store.Pending.Add(Job("ordinary", 0, DateTimeOffset.UtcNow.AddMinutes(-1)));
        store.Pending.Add(Job("important", 1, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) => Task.FromResult(request.Text)), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == completed)
            {
                completion.TrySetResult();
            }
        };
        await scheduler.StartAsync(new() { Enabled = true, Model = "test", BacklogMode = mode });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(skipped, store.Skipped);
        Assert.Equal("important", store.Results["important"]);
    }

    [Fact]
    public async Task ConcurrencyNeverExceedsFour()
    {
        var store = new MemoryTranslationJobStore();
        for (var index = 0; index < 12; index++)
        {
            store.Pending.Add(Job("message" + index, index == 11 ? 1 : 0, DateTimeOffset.UtcNow));
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var completed = 0;
        var starts = new ConcurrentQueue<string>();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient(async (request, token) =>
        {
            starts.Enqueue(request.Text);
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            if (current == 4)
            {
                fourStarted.TrySetResult();
            }
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
            return request.Text;
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) =>
        {
            if (Interlocked.Increment(ref completed) == 12)
            {
                allDone.TrySetResult();
            }
        };
        await scheduler.StartAsync(new() { Enabled = true, Model = "test", BacklogMode = TranslationBacklogMode.AllMessages });
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, active);
        Assert.Equal("message11", starts.First());
        release.SetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(maximum, 1, 4);
    }

    [Fact]
    public async Task UpdatingConfigurationCancelsOldResultAndCompletesWithNewTarget()
    {
        var store = new MemoryTranslationJobStore();
        store.Pending.Add(Job("hello", 1, DateTimeOffset.UtcNow));
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient(async (request, token) =>
        {
            if (request.SystemPrompt.Contains("zh-CN", StringComparison.Ordinal))
            {
                oldStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return "こんにちは";
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, args) => completed.TrySetResult(args.Translation);
        await scheduler.StartAsync(new() { Enabled = true, Model = "test", TargetLanguage = "zh-CN" });
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.UpdateSettingsAsync(new() { Enabled = true, Model = "test", TargetLanguage = "ja-JP" });
        Assert.Equal("こんにちは", await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("こんにちは", store.Results["hello"]);
    }

    [Fact]
    public async Task DisplayAndBacklogChangesDoNotCancelOrRepeatAnInFlightRequest()
    {
        var store = new MemoryTranslationJobStore();
        store.Pending.Add(Job("hello", 1, DateTimeOffset.UtcNow));
        var client = new ControlledTranslationClient();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();
        await scheduler.StartAsync(new() { Enabled = true, Model = "test" });
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await scheduler.UpdateSettingsAsync(new()
        {
            Enabled = true, Model = "test", DisplayMode = TranslationDisplayMode.TranslationOnly,
            BacklogMode = TranslationBacklogMode.AllMessages
        });
        client.Response.TrySetResult("你好");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, client.Calls);
        Assert.False(client.WasCancelled);
    }

    private static TranslationJob Job(string id, int priority, DateTimeOffset createdAt)
    {
        return new() { Id = id, MessageId = id, RoomTabId = "room", Text = id, Priority = priority, CreatedAt = createdAt };
    }
}
