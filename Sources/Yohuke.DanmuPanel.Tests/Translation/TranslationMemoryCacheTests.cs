using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>通过翻译服务验证有界缓存的读取、过期和清理语义。</summary>
public sealed class TranslationMemoryCacheTests
{
    /// <summary>读取命中提升其使用顺序，新增条目淘汰最久未使用的译文。</summary>
    [Fact]
    public async Task CacheHitPromotesTheEntryBeforeEviction()
    {
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
            Task.FromResult($"{request.Text} {++calls}")), cacheCapacity: 2);
        var first = Request("first");
        var second = Request("second");
        var third = Request("third");

        Assert.Equal("first 1", await service.TranslateAsync(first));
        Assert.Equal("second 2", await service.TranslateAsync(second));
        Assert.Equal("first 1", await service.TranslateAsync(first));
        Assert.Equal("third 3", await service.TranslateAsync(third));
        Assert.Equal("first 1", await service.TranslateAsync(first));
        Assert.Equal("second 4", await service.TranslateAsync(second));
    }

    /// <summary>TTL 从成功写入开始，读取命中不会延迟到期。</summary>
    [Fact]
    public async Task ExpirationStartsOnSuccessAndCacheHitsDoNotExtendIt()
    {
        var time = new TranslationCacheTestTimeProvider();
        var client = new ControlledTranslationClient();
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient((request, cancellationToken) =>
            Interlocked.Increment(ref calls) == 1 ? client.TranslateAsync(request, cancellationToken) : Task.FromResult("新译文")),
            cacheTtlMinutes: 5, timeProvider: time);
        var request = Request("hello");
        var pending = service.TranslateAsync(request);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromMinutes(6));
        client.Response.SetResult("初次译文");
        Assert.Equal("初次译文", await pending.WaitAsync(TimeSpan.FromSeconds(3)));

        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal("初次译文", await service.TranslateAsync(request));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("新译文", await service.TranslateAsync(request));
        Assert.Equal("新译文", await service.TranslateAsync(request));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>运行时缩容立即淘汰多余译文，后续扩容可以保留新译文。</summary>
    [Fact]
    public async Task ResizingKeepsOnlyTheMostRecentlyUsedEntries()
    {
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
            Task.FromResult($"{request.Text} {++calls}")), cacheCapacity: 3);
        var first = Request("first");
        var second = Request("second");
        var third = Request("third");

        Assert.Equal("first 1", await service.TranslateAsync(first));
        Assert.Equal("second 2", await service.TranslateAsync(second));
        Assert.Equal("third 3", await service.TranslateAsync(third));
        Assert.Equal("first 1", await service.TranslateAsync(first));

        service.ConfigureCache(cacheCapacity: 1, cacheTtlMinutes: 5);
        service.ConfigureCache(cacheCapacity: 3, cacheTtlMinutes: 5);

        Assert.Equal("first 1", await service.TranslateAsync(first));
        Assert.Equal("second 4", await service.TranslateAsync(second));
        Assert.Equal("third 5", await service.TranslateAsync(third));
        Assert.Equal("first 1", await service.TranslateAsync(first));
        Assert.Equal("second 4", await service.TranslateAsync(second));
        Assert.Equal("third 5", await service.TranslateAsync(third));
    }

    /// <summary>调整 TTL 时，保留条目使用已有的写入时间计算期限。</summary>
    [Fact]
    public async Task ConfiguredTtlAppliesToExistingEntries()
    {
        var time = new TranslationCacheTestTimeProvider();
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient((request, _) =>
            Task.FromResult($"{request.Text} {++calls}")), cacheCapacity: 2, cacheTtlMinutes: 5, timeProvider: time);
        var request = Request("hello");

        Assert.Equal("hello 1", await service.TranslateAsync(request));
        time.Advance(TimeSpan.FromMinutes(3));
        service.ConfigureCache(cacheCapacity: 2, cacheTtlMinutes: 2);
        Assert.Equal("hello 2", await service.TranslateAsync(request));

        time.Advance(TimeSpan.FromMinutes(1));
        service.ConfigureCache(cacheCapacity: 2, cacheTtlMinutes: 3);
        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(59));
        Assert.Equal("hello 2", await service.TranslateAsync(request));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("hello 3", await service.TranslateAsync(request));
    }

    /// <summary>容量为零时并发的相同消息仍共享一次请求，后续消息再次请求。</summary>
    [Fact]
    public async Task ZeroCapacitySharesInFlightWorkWithoutRetainingTheResult()
    {
        var client = new ControlledTranslationClient();
        await using var service = new TranslationService(client, cacheCapacity: 0);
        var request = Request("hello");

        var first = service.TranslateAsync(request);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.TranslateAsync(request);
        client.Response.SetResult("你好");

        Assert.Equal(new[] { "你好", "你好" }, await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, client.Calls);
        Assert.Equal("你好", await service.TranslateAsync(request));
        Assert.Equal(2, client.Calls);
    }

    /// <summary>清空前的请求仅交付给原等待者，清空后发起的新请求独立产生并保留结果。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearingPreventsOldRequestsFromFillingOrServingTheNewCache(bool requestBeforePreviousCompletes)
    {
        var client = new ControlledTranslationClient();
        var calls = 0;
        await using var service = new TranslationService(new DelegateTranslationClient((request, cancellationToken) =>
            Interlocked.Increment(ref calls) == 1 ? client.TranslateAsync(request, cancellationToken) : Task.FromResult("新译文")));
        var request = Request("hello");
        var previous = service.TranslateAsync(request);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        service.ClearCache();
        if (requestBeforePreviousCompletes)
        {
            Assert.Equal("新译文", await service.TranslateAsync(request).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        client.Response.SetResult("旧译文");

        Assert.Equal("旧译文", await previous.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("新译文", await service.TranslateAsync(request).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    private static TranslationRequest Request(string text)
    {
        return TranslationRequestFactory.Create(new TranslationSettings(), text)!;
    }
}
