using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class TranslationServiceTests
{
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
