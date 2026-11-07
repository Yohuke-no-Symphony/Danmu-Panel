using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class SqliteTranslationCacheTests
{
    /// <summary>连接初始化被文件写锁阻塞时，构造调用仍立即交还调用线程。</summary>
    [Fact]
    public async Task ConstructorReturnsWhileDatabaseInitializationWaitsForWriteLock()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = directory.DatabasePath;
        using var blocker = OpenConnection(path);
        var transaction = blocker.BeginTransaction(deferred: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var constructed = InvokeOnDedicatedThread(() => new SqliteTranslationCache(path), started);
        bool returnedWhileLocked;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            returnedWhileLocked = await Task.WhenAny(constructed, Task.Delay(TimeSpan.FromSeconds(1))) == constructed;
        }
        finally
        {
            transaction.Dispose();
            await using var cache = await constructed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(await cache.GetAsync("missing"));
        }
        Assert.True(returnedWhileLocked, "Cache construction performed database initialization on the calling thread.");
    }

    /// <summary>无信号量竞争时，读取命中、保存和清空的 SQLite 等锁也不能占用调用线程。</summary>
    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("clear")]
    public async Task OperationsReturnTasksWhileDatabaseWriteLockIsHeld(string operation)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var cache = new SqliteTranslationCache(directory.DatabasePath);
        await cache.SetAsync("seed", "value");
        await cache.SetAsync("newest", "newest value");
        using var blocker = OpenConnection(directory.DatabasePath);
        var transaction = blocker.BeginTransaction(deferred: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = InvokeCacheOperationOnDedicatedThread(cache, operation, started);
        bool returnedWhileLocked;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            returnedWhileLocked = await Task.WhenAny(invoked, Task.Delay(TimeSpan.FromSeconds(1))) == invoked;
            if (returnedWhileLocked)
            {
                Assert.False((await invoked).IsCompleted);
            }
        }
        finally
        {
            transaction.Dispose();
            var pending = await invoked.WaitAsync(TimeSpan.FromSeconds(10));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(returnedWhileLocked, "A cache operation performed SQLite I/O on the calling thread.");
        Assert.Equal(operation == "clear" ? null : "value", await cache.GetAsync("seed"));
        if (operation == "set")
        {
            Assert.Equal("next", await cache.GetAsync("second"));
        }
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedAndSurvivesReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "translations.db");
        try
        {
            await using (var cache = new SqliteTranslationCache(path, 2))
            {
                await cache.SetAsync("a", "A");
                await cache.SetAsync("b", "B");
                Assert.Equal("A", await cache.GetAsync("a"));
                await cache.SetAsync("c", "C");
                Assert.Null(await cache.GetAsync("b"));
            }

            await using (var cache = new SqliteTranslationCache(path, 2))
            {
                Assert.Equal("A", await cache.GetAsync("a"));
                Assert.Equal("C", await cache.GetAsync("c"));
                await cache.ClearAsync();
                Assert.Null(await cache.GetAsync("a"));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>退出立即交还调用线程，但必须等已接受的受锁写入提交后才能关闭连接。</summary>
    [Fact]
    public async Task DisposeDrainsAcceptedWritesAndRejectsFurtherOperations()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var cache = new SqliteTranslationCache(directory.DatabasePath);
        await cache.SetAsync("seed", "value");
        using var blocker = OpenConnection(directory.DatabasePath);
        var transaction = blocker.BeginTransaction(deferred: false);
        var pending = cache.SetAsync("committed-before-close", "saved");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = InvokeCacheOperationOnDedicatedThread(cache, "dispose", started);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var returned = await Task.WhenAny(invoked, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(invoked, returned);
            Assert.False((await invoked).IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync("seed"));
        }
        finally
        {
            transaction.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await (await invoked.WaitAsync(TimeSpan.FromSeconds(10))).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await using var reopened = new SqliteTranslationCache(directory.DatabasePath);
        Assert.Equal("saved", await reopened.GetAsync("committed-before-close"));
    }

    /// <summary>排队后取消的操作不得在前一项阻塞结束后继续写入。</summary>
    [Fact]
    public async Task CancellationBeforeExecutionDoesNotWriteQueuedValue()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var cache = new SqliteTranslationCache(directory.DatabasePath);
        await cache.SetAsync("seed", "value");
        using var blocker = OpenConnection(directory.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        Task pending;
        Task cancelled;
        using (blocker.BeginTransaction(deferred: false))
        {
            pending = cache.SetAsync("first", "saved");
            cancelled = cache.SetAsync("cancelled", "unwanted", cancellation.Token);
            cancellation.Cancel();
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Null(await cache.GetAsync("cancelled"));
        Assert.Equal("saved", await cache.GetAsync("first"));
    }

    /// <summary>后台初始化失败须传递给缓存操作和退出等待，不能留下永不完成的任务。</summary>
    [Fact]
    public async Task InitializationFailureReachesOperationsAndDispose()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var blockedDirectory = Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockedDirectory, "file");
        var cache = new SqliteTranslationCache(Path.Combine(blockedDirectory, "cache.db"));
        await Assert.ThrowsAnyAsync<IOException>(() => cache.GetAsync("missing").WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<IOException>(() => cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>单项 SQL 提交失败须返回原始异常，后续缓存操作仍可继续。</summary>
    [Fact]
    public async Task FailedOperationDoesNotTerminateTheWorkerOrEvictCommittedValues()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var cache = new SqliteTranslationCache(directory.DatabasePath, 2);
        await cache.SetAsync("seed", "value");
        using var connection = OpenConnection(directory.DatabasePath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_test_value BEFORE INSERT ON translations
                WHEN NEW.cache_key='rejected'
                BEGIN SELECT RAISE(ABORT, 'rejected test value'); END;
                """;
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => cache.SetAsync("rejected", "unwanted"));
        Assert.Equal("value", await cache.GetAsync("seed"));
        Assert.Null(await cache.GetAsync("rejected"));
        await cache.SetAsync("accepted", "saved");
        Assert.Equal("saved", await cache.GetAsync("accepted"));
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        connection.Open();
        return connection;
    }

    private static Task<T> InvokeOnDedicatedThread<T>(Func<T> action, TaskCompletionSource started)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            started.TrySetResult();
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }) { IsBackground = true };
        caller.Start();
        return completion.Task;
    }

    private static Task<Task> InvokeCacheOperationOnDedicatedThread(SqliteTranslationCache cache, string operation,
        TaskCompletionSource started)
    {
        return InvokeOnDedicatedThread<Task>(() => operation switch
        {
            "get" => cache.GetAsync("seed"),
            "set" => cache.SetAsync("second", "next"),
            "clear" => cache.ClearAsync(),
            "dispose" => cache.DisposeAsync().AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        }, started);
    }
}
