using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>独立数据库中的持久 LRU；淘汰不会影响消息已有译文。</summary>
public sealed class SqliteTranslationCache : ITranslationCache, IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly int capacity;
    private long accessOrder;

    /// <summary>打开缓存数据库并恢复访问序号。</summary>
    public SqliteTranslationCache(string databasePath, int capacity = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        if (databasePath != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        }

        connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS translations (cache_key TEXT PRIMARY KEY, translation TEXT NOT NULL, access_order INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_translations_access ON translations(access_order);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COALESCE(MAX(access_order), 0) FROM translations;";
        accessOrder = Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>查找并提升最近使用顺序。</summary>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>保存成功结果，并在同一事务中淘汰最久未访问项。</summary>
    public async Task SetAsync(string key, string translation, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>清空可复用缓存，不删除永久消息。</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM translations;";
            command.ExecuteNonQuery();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>等待当前操作完成并关闭数据库。</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
