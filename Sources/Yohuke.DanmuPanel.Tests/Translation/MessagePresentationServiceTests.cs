using System.Collections.Concurrent;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证注音准备的后台并发上限、独立转换器、取消及退出生命周期。</summary>
public sealed class MessagePresentationServiceTests
{
    private static readonly MessagePresentationOptions options = new(true, false, false, false, "zh-CN");

    /// <summary>转换器由各 worker 在后台独立创建，每个转换器串行使用且总并发不超过配置。</summary>
    [Fact]
    public async Task WorkersPrepareConcurrentlyWithIndependentConvertersAndABoundedDegree()
    {
        const int WORKER_COUNT = 3;
        const int REQUEST_COUNT = 24;
        var workerThreads = new ConcurrentDictionary<int, byte>();
        var factories = 0;
        var active = 0;
        var maximumActive = 0;
        var concurrentWorkers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new SemaphoreSlim(0);
        await using var service = new MessagePresentationService(() =>
        {
            Interlocked.Increment(ref factories);
            var converterActive = 0;
            return new DelegatePinyinService(_ =>
            {
                Assert.Equal(1, Interlocked.Increment(ref converterActive));
                var count = Interlocked.Increment(ref active);
                workerThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
                int previous;
                do
                {
                    previous = Volatile.Read(ref maximumActive);
                }
                while (count > previous && Interlocked.CompareExchange(ref maximumActive, count, previous) != previous);
                if (count == WORKER_COUNT)
                {
                    concurrentWorkers.TrySetResult();
                }
                try
                {
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "The test must release blocked converters.");
                    return "pīn yīn";
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    Interlocked.Decrement(ref converterActive);
                }
            });
        }, WORKER_COUNT);
        var requests = Enumerable.Range(0, REQUEST_COUNT)
            .Select(index => service.PrepareAsync(CreateRecord($"用户{index}"), options, CancellationToken.None)).ToArray();
        try
        {
            await concurrentWorkers.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WORKER_COUNT, service.WorkerCount);
            Assert.Equal(WORKER_COUNT, Volatile.Read(ref factories));
            Assert.Equal(WORKER_COUNT, Volatile.Read(ref active));
            Assert.InRange(workerThreads.Count, WORKER_COUNT, REQUEST_COUNT);
        }
        finally
        {
            release.Release(REQUEST_COUNT);
        }
        var prepared = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(prepared, snapshot => Assert.Equal("pīn yīn", snapshot.NamePinyin));
        Assert.Equal(WORKER_COUNT, maximumActive);
    }

    /// <summary>慢请求占据一个 worker 时，其他请求可先完成而不等待队首。</summary>
    [Fact]
    public async Task ABlockedRequestDoesNotPreventAnotherWorkerFromCompleting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var service = new MessagePresentationService(() => new DelegatePinyinService(text =>
        {
            if (text == "slow")
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return "prepared " + text;
        }), 2);
        var slow = service.PrepareAsync(CreateRecord("slow"), options, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var fast = await service.PrepareAsync(CreateRecord("fast"), options, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("prepared fast", fast.NamePinyin);
            Assert.False(slow.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        Assert.Equal("prepared slow", (await slow.WaitAsync(TimeSpan.FromSeconds(5))).NamePinyin);
    }

    /// <summary>排队期间取消的请求立即结束，随后出队也不调用转换器或阻塞后续请求。</summary>
    [Fact]
    public async Task CancellationSkipsQueuedConversionAndPreservesTheWorker()
    {
        var calls = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        await using var service = new MessagePresentationService(() => new DelegatePinyinService(text =>
        {
            calls.Enqueue(text);
            if (text == "first")
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return "prepared " + text;
        }), 1);
        var first = service.PrepareAsync(CreateRecord("first"), options, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var canceled = service.PrepareAsync(CreateRecord("canceled"), options, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await service.PrepareAsync(CreateRecord("last"), options, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "last"], calls);
    }

    /// <summary>退出取消在途、排队及队列背压中的等待者，同时等待正在执行的同步转换安全返回。</summary>
    [Fact]
    public async Task DisposalCancelsPendingAndBackpressuredRequestsBeforeJoiningTheRunningConverter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var service = new MessagePresentationService(() => new DelegatePinyinService(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return "prepared";
        }), 1);
        Task? disposal = null;
        try
        {
            var first = service.PrepareAsync(CreateRecord("first"), options, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = Enumerable.Range(0, 300).Select(index =>
                service.PrepareAsync(CreateRecord($"waiting{index}"), options, CancellationToken.None)).ToArray();
            disposal = service.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            foreach (var task in waiting)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.False(disposal.IsCompleted, "Shutdown must join the converter instead of abandoning a CPU worker.");
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
            disposal ??= service.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>单次转换失败只失败对应请求，后台 worker 继续处理下一条。</summary>
    [Fact]
    public async Task AConversionFailureDoesNotTerminateTheWorker()
    {
        await using var service = new MessagePresentationService(() => new DelegatePinyinService(text =>
            text == "fail" ? throw new InvalidOperationException("conversion failed") : "prepared " + text), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(CreateRecord("fail"), options, CancellationToken.None));
        var next = await service.PrepareAsync(CreateRecord("next"), options, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("prepared next", next.NamePinyin);
    }

    /// <summary>后台初始化转换器失败时，准备请求直接传播构造异常，不挂在无人消费的队列中。</summary>
    [Fact]
    public async Task AFactoryFailureFailsPreparationAndShutdownWithoutHanging()
    {
        var service = new MessagePresentationService(() => throw new InvalidOperationException("factory failed"), 2);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PrepareAsync(CreateRecord("first"), options, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("factory failed", error.Message);
        }
        finally
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private static MessageRecord CreateRecord(string userName)
    {
        return new() { RoomTabId = "presentation-workers", Kind = MessageKind.Danmu, UserName = userName, Content = "正文" };
    }
}
