using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证配置切换保留译文复用，同时隔离尚未完成的旧配置请求。</summary>
public sealed class TranslationCacheReuseTests
{
    /// <summary>服务、模型、密钥、提示词及术语变化复用译文，清空后使用新配置。</summary>
    [Fact]
    public async Task ConfigurationChangesReuseCachedTranslationUntilManuallyCleared()
    {
        var requests = new List<TranslationRequest>();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(request.Model);
        }), cache);
        var settings = new TranslationSettings { Model = "old-model", TargetLanguage = "en" };
        var original = TranslationRequestFactory.Create(settings, "主播 hello")!;
        Assert.Equal("old-model", await service.TranslateAsync(original));

        settings.BaseUrl = "https://example.invalid/v1";
        settings.Model = "new-model";
        settings.ApiKey = "new-key";
        settings.ExtraSystemPrompt = "Be concise.";
        settings.Glossary.Add(new() { Source = "主播", Target = "streamer", TargetLanguage = "en" });
        var changed = TranslationRequestFactory.Create(settings, "主播 hello")!;

        Assert.Equal("old-model", await service.TranslateAsync(changed));
        Assert.Single(requests);
        await cache.ClearAsync();
        Assert.Equal("new-model", await service.TranslateAsync(changed));
        Assert.Equal(2, requests.Count);
        Assert.Equal("new-key", requests[1].ApiKey);
        Assert.Contains("streamer", requests[1].SystemPrompt);
        Assert.Contains("Be concise.", requests[1].SystemPrompt);
        settings.TargetLanguage = "ja";
        await service.TranslateAsync(TranslationRequestFactory.Create(settings, "主播 hello")!);
        Assert.Equal(3, requests.Count);
    }

    /// <summary>相同缓存键的新配置独立请求，旧请求晚完成不会覆盖新缓存。</summary>
    [Theory]
    [InlineData("model")]
    [InlineData("key")]
    [InlineData("prompt")]
    public async Task DifferentInFlightConfigurationsDoNotShareRequestsOrOverwriteNewCache(string change)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                return await oldResponse.Task.WaitAsync(token);
            }
            return "new translation";
        }), cache);
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var previous = service.TranslateAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var changed = change switch
        {
            "model" => request with { Model = "new-model" },
            "key" => request with { ApiKey = "new-key" },
            _ => request with { SystemPrompt = "new-prompt" }
        };
        var current = service.TranslateAsync(changed);
        try
        {
            Assert.Equal("new translation", await current.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            oldResponse.TrySetResult("old translation");
        }
        Assert.Equal("old translation", await previous);
        Assert.Equal("new translation", await cache.GetAsync(request.CacheKey));
        Assert.Equal(2, calls);
    }

    /// <summary>切换 API、模型或术语时保留已完成消息，并以新配置继续处理待办。</summary>
    [Theory]
    [InlineData("api")]
    [InlineData("model")]
    [InlineData("glossary")]
    [InlineData("prompt")]
    public async Task SchedulerKeepsCompletedTranslationsAndRestartsPendingRequests(string change)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = new TranslationSettings { Enabled = true, Model = "old-model", BacklogMode = TranslationBacklogMode.AllMessages };
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var translated = new MessageRecord { RoomTabId = "tab", Kind = MessageKind.Danmu, Content = "finished" };
        var waiting = new MessageRecord { RoomTabId = "tab", Kind = MessageKind.Danmu, Content = "hello" };
        await store.AppendAsync(translated, new(), fingerprint);
        await store.ApplyTranslationAsync(translated.Id, translated.Generation, fingerprint, "保留译文");
        await store.AppendAsync(waiting, new(), fingerprint);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<TranslationRequest>();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient(async (request, token) =>
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
            return "新译文";
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();
        await scheduler.StartAsync(settings);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        switch (change)
        {
            case "api":
                settings.BaseUrl = "https://example.invalid/v1";
                break;
            case "model":
                settings.Model = "new-model";
                break;
            case "glossary":
                settings.Glossary.Add(new() { Source = "hello", Target = "您好", TargetLanguage = "zh-CN" });
                break;
            default:
                settings.ExtraSystemPrompt = "Use formal language.";
                break;
        }

        await scheduler.UpdateSettingsAsync(settings);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(fingerprint, TranslationRequestFactory.GetConfigurationFingerprint(settings));
        Assert.False(await store.UpsertTranslationJobAsync(translated.Id, translated.Generation, fingerprint));
        Assert.Equal("保留译文", (await store.GetByIdAsync(translated.Id))!.Translation);
        Assert.Equal("新译文", (await store.GetByIdAsync(waiting.Id))!.Translation);
        Assert.Equal(TranslationRequestFactory.Create(settings, waiting.Content), requests[1]);
        Assert.Equal(2, requests.Count);
    }
}
