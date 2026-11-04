using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>独立数据库中的持久 LRU；淘汰不会影响消息已有译文。</summary>
public sealed class SqliteTranslationCache : ITranslationCache, IAsyncDisposable
{
    private const int QUEUE_CAPACITY = 512;
    private readonly Channel<TranslationCacheOperation> operations = Channel.CreateBounded<TranslationCacheOperation>(
        new BoundedChannelOptions(QUEUE_CAPACITY) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private readonly int capacity;
    private long accessOrder;
    private int disposalRequested;

    /// <summary>在后台初始化缓存数据库，并串行执行缓存操作。</summary>
    public SqliteTranslationCache(string databasePath, int capacity = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        var path = databasePath == ":memory:" ? databasePath : Path.GetFullPath(databasePath);
        worker = Task.Run(() => RunAsync(path));
    }

    /// <summary>查找并提升最近使用顺序。</summary>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT translation FROM translations WHERE cache_key=$key;";
            command.Parameters.AddWithValue("$key", key);
            var value = command.ExecuteScalar() as string;
            if (value is not null)
            {
                command.CommandText = "UPDATE translations SET access_order=$order WHERE cache_key=$key;";
                command.Parameters.AddWithValue("$order", ++accessOrder);
                command.ExecuteNonQuery();
            }

            return value;
        }, cancellationToken);
    }

    /// <summary>保存成功结果，并在同一事务中淘汰最久未访问项。</summary>
    public Task SetAsync(string key, string translation, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO translations(cache_key,translation,access_order) VALUES($key,$translation,$order)
                ON CONFLICT(cache_key) DO UPDATE SET translation=excluded.translation, access_order=excluded.access_order;
                DELETE FROM translations WHERE cache_key NOT IN (SELECT cache_key FROM translations ORDER BY access_order DESC LIMIT $capacity);
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$translation", translation);
            command.Parameters.AddWithValue("$order", ++accessOrder);
            command.Parameters.AddWithValue("$capacity", capacity);
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    /// <summary>清空可复用缓存，不删除永久消息。</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM translations;";
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken);
    }

    /// <summary>排空已接受的操作，并在后台关闭数据库。</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposalRequested, 1) == 0)
        {
            operations.Writer.TryComplete();
        }
        return new(worker);
    }

    private async Task RunAsync(string databasePath)
    {
        try
        {
            if (databasePath != ":memory:")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString());
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS translations (cache_key TEXT PRIMARY KEY, translation TEXT NOT NULL, access_order INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS ix_translations_access ON translations(access_order);
                    """;
                command.ExecuteNonQuery();
                command.CommandText = "SELECT COALESCE(MAX(access_order), 0) FROM translations;";
                accessOrder = Convert.ToInt64(command.ExecuteScalar());
            }
            await foreach (var operation in operations.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                operation.Execute(connection);
            }
        }
        catch (Exception error)
        {
            operations.Writer.TryComplete(error);
            while (operations.Reader.TryRead(out var operation))
            {
                operation.Fail(error);
            }
            throw;
        }
    }

    private async Task<T> EnqueueAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalRequested) != 0, this);
        var operation = new TranslationCacheOperationOfT<T>(action, cancellationToken);
        try
        {
            await operations.Writer.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalRequested) != 0, this);
            await worker.ConfigureAwait(false);
            throw;
        }
        return await operation.Task.ConfigureAwait(false);
    }
}
