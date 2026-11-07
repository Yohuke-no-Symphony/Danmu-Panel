using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证正则屏蔽对持久翻译任务、配置切换和恢复的完整约束。</summary>
public sealed class TranslationRegexBlockingTests
{
    /// <summary>两个屏蔽开关独立阻止普通弹幕翻译，SC 始终正常翻译。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IndependentSwitchesBlockDanmuButAllowSuperChat(bool enabled, bool blockTranslation)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var danmu = Message("blocked danmu");
        var superChat = Message("blocked super chat") with { Kind = MessageKind.SuperChat };
        await store.AppendBatchAsync([danmu, superChat], new(), fingerprint);
        var requests = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedCount = 0;
        var shouldBlock = enabled || blockTranslation;
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requests.Enqueue(request.Text);
            return Task.FromResult("translated " + request.Text);
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Error += errors.Enqueue;
        scheduler.PendingChanged += (room, kind) =>
        {
            Assert.Equal("regex-room", room);
            Assert.Equal(MessageKind.Danmu, kind);
            skipped.TrySetResult();
        };
        scheduler.Completed += (_, _) =>
        {
            if (Interlocked.Increment(ref completedCount) == (shouldBlock ? 1 : 2))
            {
                completed.TrySetResult();
            }
        };

        await scheduler.StartAsync(settings, danmuFilter: Filter(enabled, blockTranslation));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shouldBlock)
        {
            await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(shouldBlock ? 1 : 2, requests.Count);
        Assert.Contains(superChat.Content, requests);
        Assert.Equal(shouldBlock ? null : "translated blocked danmu", (await store.GetByIdAsync(danmu.Id))!.Translation);
        Assert.Equal("translated blocked super chat", (await store.GetByIdAsync(superChat.Id))!.Translation);
        Assert.Empty(await store.GetPendingAsync(10));
        Assert.Empty(errors);
    }

    /// <summary>重启后放行的正则跳过任务重新排队，并切换到当前目标语言身份。</summary>
    [Fact]
    public async Task StartupRestoresPersistedRegexSkipBeforeChangingTargetLanguage()
    {
        using var directory = new MessageStoreTestDirectory();
        var message = Message("blocked persisted");
        var previousSettings = Settings();
        var previousFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(previousSettings);
        await using (var original = new SqliteMessageStore(directory.DatabasePath))
        {
            await original.AppendAsync(message, new(), previousFingerprint);
            Assert.True(await original.FailAsync(Assert.Single(await original.GetPendingAsync(10)), "skipped:regex"));
        }
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var currentSettings = Settings();
        currentSettings.TargetLanguage = "ja-JP";
        var currentFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(currentSettings);
        var requestSeen = new TaskCompletionSource<TranslationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requestSeen.TrySetResult(request);
            return Task.FromResult("復元した翻訳");
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();

        await scheduler.StartAsync(currentSettings);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var request = await requestSeen.Task;
        Assert.Contains("ja-JP", request.SystemPrompt, StringComparison.Ordinal);
        var restored = (await store.GetByIdAsync(message.Id))!;
        Assert.Equal(currentFingerprint, restored.TranslationFingerprint);
        Assert.NotEqual(previousFingerprint, restored.TranslationFingerprint);
        Assert.Equal("復元した翻訳", restored.Translation);
        Assert.Empty(await store.GetPendingAsync(10));
    }

    /// <summary>翻译关闭时不恢复持久跳过任务，重新启用后再恢复。</summary>
    [Fact]
    public async Task DisabledTranslationDefersRestoringRegexSkippedJobs()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var message = Message("blocked disabled");
        await store.AppendAsync(message, new(), TranslationRequestFactory.GetConfigurationFingerprint(settings));
        Assert.True(await store.FailAsync(Assert.Single(await store.GetPendingAsync(10)), "skipped:regex"));
        var client = new ControlledTranslationClient();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();
        settings.Enabled = false;

        await scheduler.StartAsync(settings);

        Assert.Empty(await store.GetPendingAsync(10));
        Assert.Equal(0, client.Calls);
        settings.Enabled = true;
        await scheduler.UpdateSettingsAsync(settings);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Response.TrySetResult("restored");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("restored", (await store.GetByIdAsync(message.Id))!.Translation);
    }

    /// <summary>开启屏蔽取消在途请求，关闭屏蔽后相同语言身份的任务仍能完成。</summary>
    [Fact]
    public async Task ChangingFilterCancelsInFlightAndUnblockingRestoresTheSameFingerprint()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var message = Message("blocked in flight");
        await store.AppendAsync(message, new(), fingerprint);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var errors = new ConcurrentQueue<string>();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
            return "current translation";
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Error += errors.Enqueue;
        scheduler.PendingChanged += (_, _) => skipped.TrySetResult();
        scheduler.Completed += (_, _) => completed.TrySetResult();
        await scheduler.StartAsync(settings);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.UpdateSettingsAsync(settings, danmuFilter: Filter(false, true));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
        Assert.False(completed.Task.IsCompleted);
        Assert.Equal(fingerprint, (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
        Assert.Empty(await store.GetPendingAsync(10));
        await scheduler.UpdateSettingsAsync(settings, danmuFilter: Filter(false, false));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal("current translation", (await store.GetByIdAsync(message.Id))!.Translation);
        Assert.Equal(fingerprint, (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
        Assert.Empty(errors);
    }

    /// <summary>手动重译仍服从正则屏蔽，恢复后保留跳过缓存的请求意图。</summary>
    [Fact]
    public async Task RetriedTranslationRemainsBlockedAndRefreshesAfterUnblocking()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var message = Message("blocked retry");
        await store.AppendAsync(message, new(), fingerprint);
        Assert.True(await store.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, "old translation"));
        Assert.True(await store.RetryTranslationJobAsync(message.Id, message.Generation, fingerprint));
        Assert.NotEmpty(Assert.Single(await store.GetPendingAsync(10)).RefreshId);
        var client = new ControlledTranslationClient();
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        var request = TranslationRequestFactory.Create(settings, message.Content)!;
        await cache.SetAsync(request.CacheKey, "cached old translation");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.PendingChanged += (_, _) => skipped.TrySetResult();
        scheduler.Completed += (_, _) => completed.TrySetResult();

        await scheduler.StartAsync(settings, danmuFilter: Filter(false, true));
        await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, client.Calls);
        Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
        await scheduler.UpdateSettingsAsync(settings, danmuFilter: Filter(false, false));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Response.TrySetResult("fresh translation");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Calls);
        Assert.Equal("fresh translation", (await store.GetByIdAsync(message.Id))!.Translation);
        Assert.Equal("fresh translation", await cache.GetAsync(request.CacheKey));
    }

    /// <summary>同一有效翻译规则的重新保存和显示模式切换不取消或重复请求。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentTranslationRulesKeepInFlightRequest(bool switchToFullBlocking)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        await store.AppendAsync(Message("allowed in flight"), new(), TranslationRequestFactory.GetConfigurationFingerprint(settings));
        var client = new ControlledTranslationClient();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(client, cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();
        await scheduler.StartAsync(settings, danmuFilter: Filter(false, true));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.UpdateSettingsAsync(settings, danmuFilter: Filter(switchToFullBlocking, !switchToFullBlocking));
        client.Response.TrySetResult("translation");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, client.Calls);
        Assert.False(client.WasCancelled);
    }

    /// <summary>重新保存同名规则复位超时暂停后，取消此前因暂停而放行的在途请求。</summary>
    [Fact]
    public async Task ResavingTimedOutRuleCancelsPreviouslyAllowedInFlightRequest()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var filterSettings = new DanmuRegexFilterSettings { Rules = [new() { Pattern = "^(a+)+$", BlockTranslation = true }] };
        var suspendedFilter = new DanmuRegexFilter(filterSettings);
        Assert.False(suspendedFilter.IsTranslationBlocked(new string('a', 100_000) + "!"));
        Assert.Equal(1, suspendedFilter.StateVersion);
        var message = Message("aaa");
        await store.AppendAsync(message, new(), fingerprint);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new ConcurrentQueue<string>();
        var calls = 0;
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
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
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.PendingChanged += (_, _) => skipped.TrySetResult();
        scheduler.Completed += (_, args) => completions.Enqueue(args.Translation);
        await scheduler.StartAsync(settings, danmuFilter: suspendedFilter);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.UpdateSettingsAsync(settings, danmuFilter: new(filterSettings));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Empty(completions);
        Assert.Empty(await store.GetPendingAsync(10));
        Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
        Assert.Equal(fingerprint, (await store.GetByIdAsync(message.Id))!.TranslationFingerprint);
    }

    /// <summary>省略过滤参数只更新翻译设置，保留已屏蔽任务并正常处理新放行消息。</summary>
    [Fact]
    public async Task UpdatingTranslationOptionsWithoutFilterKeepsRegexBlocking()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var blocked = Message("blocked retained");
        var allowed = Message("allowed after settings update");
        await store.AppendAsync(blocked, new(), fingerprint);
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requests.Enqueue(request.Text);
            return Task.FromResult("translated " + request.Text);
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.PendingChanged += (_, _) => skipped.TrySetResult();
        scheduler.Completed += (_, args) =>
        {
            if (args.MessageId == allowed.Id)
            {
                completed.TrySetResult();
            }
        };
        await scheduler.StartAsync(settings, danmuFilter: Filter(false, true));
        await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.DisplayMode = TranslationDisplayMode.TranslationOnly;
        settings.BacklogMode = TranslationBacklogMode.Realtime;

        await scheduler.UpdateSettingsAsync(settings);
        await store.AppendAsync(allowed, new(), fingerprint);
        scheduler.NotifyWorkAvailable();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(allowed.Content, Assert.Single(requests));
        Assert.Null((await store.GetByIdAsync(blocked.Id))!.Translation);
        Assert.Equal("translated " + allowed.Content, (await store.GetByIdAsync(allowed.Id))!.Translation);
        Assert.Empty(await store.GetPendingAsync(10));
    }

    /// <summary>超过一次读取数量的屏蔽任务不会饿死其后可翻译的普通消息。</summary>
    [Fact]
    public async Task BlockedBacklogLargerThanReadBatchDoesNotStarveAllowedMessage()
    {
        var store = new MemoryTranslationJobStore();
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var createdAt = DateTimeOffset.UtcNow;
        for (var index = 0; index < 220; index++)
        {
            store.Pending.Add(new()
            {
                Id = "blocked" + index, MessageId = "blocked" + index, RoomTabId = "regex-room",
                Kind = MessageKind.Danmu, Text = "blocked " + index,
                ConfigurationFingerprint = fingerprint, CreatedAt = createdAt.AddMilliseconds(index)
            });
        }
        store.Pending.Add(new()
        {
            Id = "allowed", MessageId = "allowed", RoomTabId = "regex-room", Kind = MessageKind.Danmu,
            Text = "allowed", ConfigurationFingerprint = fingerprint, CreatedAt = createdAt.AddSeconds(1)
        });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requests.Enqueue(request.Text);
            return Task.FromResult("translated");
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Completed += (_, _) => completed.TrySetResult();

        await scheduler.StartAsync(settings, danmuFilter: Filter(false, true));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("allowed", Assert.Single(requests));
        Assert.Equal("translated", store.Results["allowed"]);
        Assert.Equal(220, store.RegexBlockedCount);
        Assert.Empty(await store.GetPendingAsync(300));
    }

    /// <summary>规则在调度或启动恢复扫描中超时后，无需保存即可恢复此前跳过的消息。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutRuleRestoresSkippedJobsWithoutSavingSettings(bool timeoutDuringStartupRestore)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var settings = Settings();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var previouslyBlocked = Message("aaa") with { Id = "aaa-first" };
        var pathological = Message(new string('a', 100_000) + "!") with { Id = "zzz-last" };
        await store.AppendAsync(previouslyBlocked, new(), fingerprint);
        if (timeoutDuringStartupRestore)
        {
            await store.AppendAsync(pathological, new(), fingerprint);
            foreach (var job in await store.GetPendingAsync(10))
            {
                Assert.True(await store.FailAsync(job, "skipped:regex"));
            }
        }
        var filter = new DanmuRegexFilter(new() { Rules = [new() { Pattern = "^(a+)+$", BlockTranslation = true }] });
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var completedCount = 0;
        filter.PatternTimedOut += _ => timedOut.TrySetResult();
        await using var cache = new SqliteTranslationCache(":memory:");
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
        {
            requests.Enqueue(request.Text);
            return Task.FromResult(request.Text == "aaa" ? "restored short message" : "long message");
        }), cache);
        await using var scheduler = new TranslationScheduler(store, service);
        scheduler.Error += errors.Enqueue;
        scheduler.PendingChanged += (_, _) => skipped.TrySetResult();
        scheduler.Completed += (_, _) =>
        {
            if (Interlocked.Increment(ref completedCount) == 2)
            {
                completed.TrySetResult();
            }
        };

        await scheduler.StartAsync(settings, danmuFilter: filter);
        if (!timeoutDuringStartupRestore)
        {
            await skipped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(requests);
            Assert.Empty(await store.GetPendingAsync(10));
            await store.AppendAsync(pathological, new(), fingerprint);
            scheduler.NotifyWorkAvailable();
        }
        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, filter.StateVersion);
        Assert.Equal(2, requests.Count);
        Assert.Contains("aaa", requests);
        Assert.Equal("restored short message", (await store.GetByIdAsync(previouslyBlocked.Id))!.Translation);
        Assert.Empty(await store.GetPendingAsync(10));
        Assert.Empty(errors);
    }

    /// <summary>只恢复正则跳过且当前放行、消息与房间代次和身份都有效的任务。</summary>
    [Fact]
    public async Task RestoringRegexSkipsPreservesFailuresAndRejectsStaleRecords()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var messages = new[]
        {
            Message("allowed"), Message("blocked remains"), Message("network failure"),
            Message("emoticon skip"), Message("stale message"), Message("stale room"), Message("stale identity")
        };
        foreach (var message in messages)
        {
            await store.AppendAsync(message with { RoomTabId = message.Id }, new(), "configuration");
        }
        foreach (var job in await store.GetPendingAsync(20))
        {
            var error = job.Text switch
            {
                "network failure" => "translation.failed",
                "emoticon skip" => "skipped:emoticon",
                _ => "skipped:regex"
            };
            Assert.True(await store.FailAsync(job, error));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE message_records SET payload=json_set(payload,'$.Generation',1) WHERE id=$message;
                UPDATE message_rooms SET generation=1 WHERE id=$room;
                UPDATE message_records SET payload=json_set(payload,'$.TranslationFingerprint','changed') WHERE id=$identity;
                """;
            command.Parameters.AddWithValue("$message", messages[4].Id);
            command.Parameters.AddWithValue("$room", messages[5].Id);
            command.Parameters.AddWithValue("$identity", messages[6].Id);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, await store.RestoreRegexBlockedAsync(Filter(false, true)));
        Assert.Equal(messages[0].Id, Assert.Single(await store.GetPendingAsync(20)).MessageId);
        Assert.Equal(0, await store.RestoreRegexBlockedAsync(Filter(false, true)));
        Assert.Equal(1, await store.RestoreRegexBlockedAsync(Filter(false, false)));
        Assert.Equal(new[] { messages[0].Id, messages[1].Id }.Order(StringComparer.Ordinal),
            (await store.GetPendingAsync(20)).Select(job => job.MessageId).Order(StringComparer.Ordinal));
    }

    private static TranslationSettings Settings()
    {
        return new() { Enabled = true, Model = "test", BacklogMode = TranslationBacklogMode.AllMessages };
    }

    private static DanmuRegexFilter Filter(bool enabled, bool blockTranslation)
    {
        return new(new() { Rules = [new() { Pattern = "^blocked", BlockDisplay = enabled, BlockTranslation = blockTranslation }] });
    }

    private static MessageRecord Message(string text)
    {
        return new() { RoomTabId = "regex-room", Kind = MessageKind.Danmu, Content = text };
    }
}
