using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class TranslationServiceTests
{
    /// <summary>强制重译立即请求模型，旧在途请求随后成功或失败都不覆盖新缓存。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshMakesANewRequestWithoutWaitingForAnOlderRequest(bool previousFails)
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var previousStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                previousStarted.TrySetResult();
                return await previousResponse.Task.WaitAsync(token);
            }
            return "新译文";
        }), cache);
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var previous = service.TranslateAsync(request);
        await previousStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var refreshed = service.RefreshAsync(request);
        Assert.Equal("新译文", await refreshed.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(previous.IsCompleted);
        if (previousFails)
        {
            previousResponse.SetException(new TranslationException("failed"));
            await Assert.ThrowsAsync<TranslationException>(() => previous);
        }
        else
        {
            previousResponse.SetResult("旧译文");
            Assert.Equal("旧译文", await previous);
        }

        Assert.Equal("新译文", await service.TranslateAsync(request));
        Assert.Equal("新译文", await cache.GetAsync(request.CacheKey));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>连续强制重译分别调用模型，即使先发请求最后返回也保留最新请求的缓存。</summary>
    [Fact]
    public async Task ConcurrentRefreshesEachRequestTheModelAndKeepTheLatestResultCached()
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                return await firstResponse.Task.WaitAsync(token);
            }
            return "第二次重译";
        }), cache);
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        await cache.SetAsync(request.CacheKey, "缓存译文");

        var first = service.RefreshAsync(request);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.RefreshAsync(request);
        Assert.Equal("第二次重译", await second.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(first.IsCompleted);
        firstResponse.SetResult("第一次重译");

        Assert.Equal("第一次重译", await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("第二次重译", await second.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("第二次重译", await service.TranslateAsync(request));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>取消重译不影响旧请求的等待者，也不允许被取代的旧请求更新缓存。</summary>
    [Fact]
    public async Task CancellingARefreshKeepsTheOlderRequestAlive()
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var previousStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new DelegateTranslationClient(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                previousStarted.TrySetResult();
                return await previousResponse.Task.WaitAsync(token);
            }
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });
        await using var service = new TranslationService(client, cache);
        using var cancellation = new CancellationTokenSource();
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var previous = service.TranslateAsync(request);
        await previousStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var refreshed = service.RefreshAsync(request, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshed);
        previousResponse.SetResult("旧译文");

        Assert.Equal("旧译文", await previous.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(await cache.GetAsync(request.CacheKey));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ConcurrentDuplicateMessagesShareOneRequestAndSubsequentRequestUsesCache()
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var client = new ControlledTranslationClient();
        await using var service = new TranslationService(client, cache);
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var calls = Enumerable.Range(0, 20).Select(_ => service.TranslateAsync(request)).ToArray();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, client.Calls);
        client.Response.SetResult("你好");
        Assert.All(await Task.WhenAll(calls), result => Assert.Equal("你好", result));
        Assert.Equal("你好", await service.TranslateAsync(request));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task CancellingOneWaiterKeepsOtherMessagesTranslationAlive()
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var client = new ControlledTranslationClient();
        await using var service = new TranslationService(client, cache);
        using var cancellation = new CancellationTokenSource();
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var first = service.TranslateAsync(request, cancellation.Token);
        var second = service.TranslateAsync(request);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        client.Response.SetResult("你好");
        Assert.Equal("你好", await second);
        Assert.False(client.WasCancelled);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task FailuresAreNotCached()
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var client = new ControlledTranslationClient();
        await using var service = new TranslationService(client, cache);
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var first = service.TranslateAsync(request);
        client.Response.SetException(new TranslationException("failed"));
        await Assert.ThrowsAsync<TranslationException>(() => first);
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(request));
        Assert.Equal(2, client.Calls);
        Assert.Null(await cache.GetAsync(request.CacheKey));
    }

    [Fact]
    public async Task CancellingAllWaitersCancelsTheSharedNetworkRequest()
    {
        await using var cache = new SqliteTranslationCache(":memory:");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegateTranslationClient(async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return "unreachable";
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        await using var service = new TranslationService(client, cache);
        using var cancellation = new CancellationTokenSource();
        var request = TranslationRequestFactory.Create(new TranslationSettings(), "hello")!;
        var pending = service.TranslateAsync(request, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(await cache.GetAsync(request.CacheKey));
    }

}
