using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services.Messages;

/// <summary>串行提交 SQLite 事务，成功提交后才向调用方返回可见消息。</summary>
public sealed partial class SqliteMessageStore : IMessageStore, ITranslationJobStore, ITestMessageStore
{
    private const int QUEUE_CAPACITY = 512;
    private const int DATABASE_VERSION = 4;
    private readonly Channel<MessageStoreOperation> operations = Channel.CreateBounded<MessageStoreOperation>(
        new BoundedChannelOptions(QUEUE_CAPACITY) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MessageRecordReadCache readCache = new();
    private readonly Task worker;

    /// <summary>创建独立消息数据库并启动有界写入队列。</summary>
    public SqliteMessageStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        worker = Task.Run(() => RunAsync(Path.GetFullPath(databasePath)));
    }

    /// <summary>完成已排队的提交，然后关闭数据库。</summary>
    public async ValueTask DisposeAsync()
    {
        operations.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }

    private async Task RunAsync(string databasePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            Initialize(connection);
            ready.TrySetResult();
            await foreach (var operation in operations.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                operation.Execute(connection);
            }
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            operations.Writer.TryComplete(error);
            while (operations.Reader.TryRead(out var operation))
            {
                operation.Fail(error);
            }
        }
        finally
        {
            readCache.Clear();
        }
    }

    private async Task<T> EnqueueAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operation = new MessageStoreOperationOfT<T>(action, cancellationToken);
        await operations.Writer.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
        return await operation.Task.ConfigureAwait(false);
    }

    private void Initialize(SqliteConnection connection)
    {
        connection.CreateFunction<string, string, bool>("is_emoticon_only", (content, emoticons) =>
            TranslationTextExtractor.IsEmoticonOnly(content,
                JsonSerializer.Deserialize<MessageEmoticon[]>(emoticons)!.Select(emoticon => emoticon.Text)),
            isDeterministic: true);
        Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        using var version = Command(connection, null, "PRAGMA user_version;");
        var databaseVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (databaseVersion > DATABASE_VERSION)
        {
            throw new InvalidDataException("The message database was created by a newer application version.");
        }

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS message_rooms (
                id TEXT PRIMARY KEY, generation INTEGER NOT NULL DEFAULT 0, cleared_before INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS message_feed_clears (
                room_tab_id TEXT NOT NULL, kind INTEGER NOT NULL, cleared_before INTEGER NOT NULL,
                PRIMARY KEY(room_tab_id,kind));
            CREATE TABLE IF NOT EXISTS message_records (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE,
                room_tab_id TEXT NOT NULL, kind INTEGER NOT NULL, occurred_ms INTEGER NOT NULL,
                generation INTEGER NOT NULL, priority INTEGER NOT NULL, pinned_ms INTEGER NOT NULL,
                payload TEXT NOT NULL, is_test INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_message_page ON message_records
                (room_tab_id, kind, priority DESC, pinned_ms DESC, occurred_ms DESC, sequence DESC);
            CREATE INDEX IF NOT EXISTS ix_message_recent ON message_records
                (room_tab_id, kind, occurred_ms DESC, sequence DESC);
            CREATE INDEX IF NOT EXISTS ix_message_unread ON message_records
                (room_tab_id, kind, json_extract(payload,'$.IsRead'));
            CREATE TABLE IF NOT EXISTS message_events (
                room_tab_id TEXT NOT NULL, kind INTEGER NOT NULL, source_id TEXT NOT NULL,
                PRIMARY KEY(room_tab_id,kind,source_id));
            CREATE TABLE IF NOT EXISTS message_combos (
                room_tab_id TEXT NOT NULL, combo_key TEXT NOT NULL, quantity INTEGER NOT NULL,
                total_price TEXT NOT NULL, cleared_quantity INTEGER NOT NULL DEFAULT 0,
                cleared_total_price TEXT NOT NULL DEFAULT '0', message_id TEXT,
                PRIMARY KEY(room_tab_id,combo_key));
            CREATE TABLE IF NOT EXISTS message_options (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS translation_jobs (
                id TEXT PRIMARY KEY, message_id TEXT NOT NULL UNIQUE REFERENCES message_records(id) ON DELETE CASCADE,
                room_tab_id TEXT NOT NULL, generation INTEGER NOT NULL, fingerprint TEXT NOT NULL,
                priority INTEGER NOT NULL, created_ms INTEGER NOT NULL, status INTEGER NOT NULL DEFAULT 0,
                error TEXT, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_translation_pending ON translation_jobs(status,priority DESC,created_ms);
            """);
        if (databaseVersion == 1)
        {
            Execute(connection, transaction, "ALTER TABLE message_records ADD COLUMN is_test INTEGER NOT NULL DEFAULT 0;");
        }
        Execute(connection, transaction,
            "CREATE INDEX IF NOT EXISTS ix_message_test ON message_records(room_tab_id,is_test);");
        if (databaseVersion < DATABASE_VERSION)
        {
            ReevaluateGiftPriorities(connection, transaction, ReadOptions(connection, transaction));
            Execute(connection, transaction, $"PRAGMA user_version={DATABASE_VERSION};");
        }
        transaction.Commit();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static MessageRecord? ReadMessage(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction, "SELECT payload FROM message_records WHERE id=$id;", ("$id", id));
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<MessageRecord>(payload)! : null;
    }

    private void WriteMessage(SqliteConnection connection, SqliteTransaction transaction, MessageRecord record)
    {
        readCache.Invalidate(record.Id);
        Execute(connection, transaction, """
            INSERT INTO message_records(id,room_tab_id,kind,occurred_ms,generation,priority,pinned_ms,payload,is_test)
            VALUES($id,$room,$kind,$time,$generation,$priority,$pinned,$payload,$test)
            ON CONFLICT(id) DO UPDATE SET priority=excluded.priority,pinned_ms=excluded.pinned_ms,payload=excluded.payload,is_test=excluded.is_test;
            """, ("$id", record.Id), ("$room", record.RoomTabId), ("$kind", (int)record.Kind),
            ("$time", record.Time.ToUnixTimeMilliseconds()), ("$generation", record.Generation),
            ("$priority", record.IsPriority ? 1 : 0), ("$pinned", record.PinnedAt?.ToUnixTimeMilliseconds() ?? 0),
            ("$payload", JsonSerializer.Serialize(record)), ("$test", record.IsTest ? 1 : 0));
    }

    private static MessageReadOptions ReadOptions(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT payload FROM message_options WHERE id=1;");
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<MessageReadOptions>(payload)! : new();
    }

    private static void WriteOptions(SqliteConnection connection, SqliteTransaction transaction, MessageReadOptions options)
    {
        Execute(connection, transaction, """
            INSERT INTO message_options(id,payload) VALUES(1,$payload)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload WHERE message_options.payload<>excluded.payload;
            """,
            ("$payload", JsonSerializer.Serialize(options)));
    }

    private static long ReadGeneration(SqliteConnection connection, SqliteTransaction? transaction, string roomTabId)
    {
        using var command = Command(connection, transaction, "SELECT generation FROM message_rooms WHERE id=$room;", ("$room", roomTabId));
        return command.ExecuteScalar() is long generation ? generation : 0;
    }

    private static bool IsPriority(MessageRecord record, MessageReadOptions options)
    {
        return record.Kind == MessageKind.Danmu ? record.IsPinned :
            !record.IsRead && (record.Kind != MessageKind.Gift || record.TotalPrice >= options.GiftAutoPinMinimum);
    }
}
