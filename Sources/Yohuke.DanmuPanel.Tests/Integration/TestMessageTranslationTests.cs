using System.Threading.Channels;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Translation;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证本地测试弹幕与 SC 使用真实任务持久化、调度和缓存流程。</summary>
public sealed class TestMessageTranslationTests
{
    /// <summary>测试消息以当前配置和房间代次入队，完成后可由当前设置的界面投影显示。</summary>
    [Theory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task TestTextQueuesCurrentConfigurationAndDisplaysCompletedTranslation(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await using var cache = new SqliteTranslationCache(Path.Combine(directory.Path, "translations.db"));
        var requested = new TaskCompletionSource<TranslationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var translationService = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requested.TrySetResult(request);
            return Task.FromResult("配信ありがとうございます");
        }), cache);
        await using var scheduler = new TranslationScheduler(store, translationService);
        var completion = new TaskCompletionSource<TranslationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Completed += (_, args) => completion.TrySetResult(args);
        scheduler.Error += error => completion.TrySetException(new InvalidOperationException(error));
        var room = new RoomTabState { Id = "test-room", RoomId = "123" };
        var settings = CreateSettings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var notifications = 0;
        var changed = new List<string>();
        var service = new TestMessageService(store, () => settings, changed.Add, () =>
        {
            notifications++;
            scheduler.NotifyWorkAvailable();
        });
        await store.GetGenerationAsync(room.Id);
        await store.ClearRoomAsync(room.Id);
        var generation = await store.GetGenerationAsync(room.Id);

        Assert.Equal(1, await service.AddAsync(room, new()
        {
            Kind = kind, UserName = "test viewer", Content = "Thank you for the stream", UnitPrice = 30
        }));

        var queued = Assert.Single(await store.LoadPageAsync(room.Id, kind, 0, 10));
        var job = Assert.Single(await store.GetPendingAsync(10));
        Assert.True(queued.IsTest);
        Assert.True(generation > 0);
        Assert.Equal(generation, queued.Generation);
        Assert.Equal(queued.Id, job.MessageId);
        Assert.Equal(room.Id, job.RoomTabId);
        Assert.Equal(generation, job.Generation);
        Assert.Equal(queued.Content, job.Text);
        Assert.Equal(kind == MessageKind.SuperChat ? 1 : 0, job.Priority);
        Assert.Equal(fingerprint, job.ConfigurationFingerprint);
        Assert.Equal(fingerprint, queued.TranslationFingerprint);
        Assert.Null(queued.Translation);
        Assert.Equal(1, notifications);
        Assert.Equal(new[] { room.Id }, changed);

        await scheduler.StartAsync(settings.Translation);
        var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var translated = Assert.IsType<MessageRecord>(await store.GetByIdAsync(queued.Id));
        Assert.Equal(queued.Id, completed.MessageId);
        Assert.Equal(generation, completed.Generation);
        Assert.Equal(queued.Content, request.Text);
        Assert.Equal(settings.Translation.BaseUrl, request.BaseUrl);
        Assert.Equal(settings.Translation.Model, request.Model);
        Assert.Equal(fingerprint, request.ConfigurationFingerprint);
        Assert.Equal("配信ありがとうございます", translated.Translation);
        Assert.Equal(fingerprint, translated.TranslationFingerprint);
        Assert.Empty(await store.GetPendingAsync(10));

        var item = new MessageItemViewModel(translated, settings, new PinyinService());
        Assert.True(item.HasTranslation);
        Assert.Equal("配信ありがとうございます", item.Translation);
        Assert.False(item.ShowOriginal);
        settings.Translation.TargetLanguage = "en-US";
        item.Update(translated, settings);
        Assert.False(item.HasTranslation);
        Assert.True(item.ShowOriginal);
    }

    /// <summary>先完成弹幕再加入相同正文的 SC 时，第二条消息复用缓存且保留独立消息和译文。</summary>
    [Fact]
    public async Task LaterTestSuperChatReusesCompletedDanmuTranslationCache()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await using var cache = new SqliteTranslationCache(Path.Combine(directory.Path, "translations.db"));
        var calls = 0;
        await using var translationService = new TranslationService(new DelegateTranslationClient((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("こんばんは");
        }), cache);
        await using var scheduler = new TranslationScheduler(store, translationService);
        var completions = Channel.CreateUnbounded<TranslationCompletedEventArgs>();
        scheduler.Completed += (_, args) => completions.Writer.TryWrite(args);
        scheduler.Error += error => completions.Writer.TryComplete(new InvalidOperationException(error));
        var settings = CreateSettings();
        var room = new RoomTabState { Id = "test-room", RoomId = "123" };
        var changed = new List<string>();
        var service = new TestMessageService(store, () => settings, changed.Add, () => scheduler.NotifyWorkAvailable());
        await scheduler.StartAsync(settings.Translation);

        Assert.Equal(1, await service.AddAsync(room, new()
        {
            Kind = MessageKind.Danmu, UserName = "first viewer", Content = "Good evening"
        }));
        var first = await completions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
        var request = Assert.IsType<TranslationRequest>(TranslationRequestFactory.Create(settings.Translation, "Good evening", []));
        Assert.Equal("こんばんは", await cache.GetAsync(request.CacheKey));

        Assert.Equal(1, await service.AddAsync(room, new()
        {
            Kind = MessageKind.SuperChat, UserName = "second viewer", Content = "Good evening", UnitPrice = 50
        }));
        var second = await completions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(new[] { room.Id, room.Id }, changed);
        Assert.Empty(await store.GetPendingAsync(10));
        var danmu = Assert.Single(await store.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 10));
        var superChat = Assert.Single(await store.LoadPageAsync(room.Id, MessageKind.SuperChat, 0, 10));
        Assert.Equal(first.MessageId, danmu.Id);
        Assert.Equal(second.MessageId, superChat.Id);
        Assert.All(new[] { danmu, superChat }, message =>
        {
            Assert.True(message.IsTest);
            Assert.Equal("Good evening", message.Content);
            Assert.Equal("こんばんは", message.Translation);
            Assert.Equal(request.ConfigurationFingerprint, message.TranslationFingerprint);
        });
    }

    private static PanelSettings CreateSettings()
    {
        return new()
        {
            Translation = new()
            {
                Enabled = true,
                BaseUrl = "https://translation.invalid/v1",
                ApiKey = "test-key",
                Model = "test-model",
                TargetLanguage = "ja-JP",
                DisplayMode = TranslationDisplayMode.TranslationOnly,
                BacklogMode = TranslationBacklogMode.AllMessages
            }
        };
    }
}
