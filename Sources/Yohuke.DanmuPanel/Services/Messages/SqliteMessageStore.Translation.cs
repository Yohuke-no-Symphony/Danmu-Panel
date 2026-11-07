using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    /// <inheritdoc />
    public Task<bool> UpsertTranslationJobAsync(string id, long generation, string fingerprint,
        CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var queued = UpsertTranslationJob(connection, transaction, id, generation, fingerprint, null, false);
            transaction.Commit();
            return queued;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RetryTranslationJobAsync(string id, long generation, string fingerprint,
        CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var queued = UpsertTranslationJob(connection, transaction, id, generation, fingerprint, null, false, true);
            transaction.Commit();
            return queued;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> UpsertTranslationJobsAsync(IReadOnlyList<MessageRecord> messages,
        string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messages.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(messages.Count, IMessageStore.MAX_BATCH_COUNT);
        var snapshot = messages.ToArray();
        return EnqueueAsync<IReadOnlySet<string>>(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var queued = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in snapshot)
            {
                if (queued.Contains(message.Id))
                {
                    continue;
                }
                if (UpsertTranslationJob(connection, transaction, message.Id, message.Generation, fingerprint,
                        message.TranslationFingerprint, true))
                {
                    queued.Add(message.Id);
                }
            }
            transaction.Commit();
            return queued;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ApplyTranslationAsync(string id, long generation, string fingerprint, string translation,
        CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var applied = ApplyTranslation(connection, transaction, id, generation, fingerprint, translation);
            if (applied)
            {
                Execute(connection, transaction, "DELETE FROM translation_jobs WHERE message_id=$id AND generation=$generation AND fingerprint=$fingerprint;",
                    ("$id", id), ("$generation", generation), ("$fingerprint", fingerprint));
            }
            transaction.Commit();
            return applied;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TranslationJob>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return EnqueueAsync<IReadOnlyList<TranslationJob>>(connection =>
        {
            using var command = Command(connection, null,
                "SELECT payload FROM translation_jobs WHERE status=0 ORDER BY priority DESC,created_ms,id LIMIT $limit;", ("$limit", limit));
            return ReadJobs(command);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(TranslationJob job, string translation, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            if (!MatchesPendingJob(connection, transaction, job))
            {
                return false;
            }
            var applied = ApplyTranslation(connection, transaction, job.MessageId, job.Generation,
                job.ConfigurationFingerprint, translation);
            if (applied)
            {
                Execute(connection, transaction, "DELETE FROM translation_jobs WHERE id=$id;", ("$id", job.Id));
            }
            transaction.Commit();
            return applied;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> FailAsync(TranslationJob job, string error, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection => Execute(connection, null, """
            UPDATE translation_jobs SET status=1,error=$error
            WHERE id=$id AND generation=$generation AND fingerprint=$fingerprint AND status=0
                AND COALESCE(json_extract(payload,'$.RefreshId'),'')=$refresh;
            """, ("$error", error), ("$id", job.Id), ("$generation", job.Generation),
            ("$fingerprint", job.ConfigurationFingerprint), ("$refresh", job.RefreshId)) > 0, cancellationToken);
    }

    /// <inheritdoc />
    public Task ResetPendingAsync(string configurationFingerprint, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction,
                "SELECT payload FROM translation_jobs WHERE status=0 AND fingerprint<>$fingerprint;",
                ("$fingerprint", configurationFingerprint));
            foreach (var job in ReadJobs(command))
            {
                var record = ReadMessage(connection, transaction, job.MessageId);
                if (record is null || record.Generation != job.Generation)
                {
                    continue;
                }
                record = record with { TranslationFingerprint = configurationFingerprint, Translation = null };
                WriteMessage(connection, transaction, record);
                WriteJob(connection, transaction, job with { ConfigurationFingerprint = configurationFingerprint });
            }
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> RestoreRegexBlockedAsync(DanmuRegexFilter filter, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction,
                "SELECT payload FROM translation_jobs WHERE status=1 AND error='skipped:regex' ORDER BY created_ms,id;");
            var restored = 0;
            foreach (var job in ReadJobs(command))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ReadMessage(connection, transaction, job.MessageId);
                if (record is null || record.Generation != job.Generation || record.RoomTabId != job.RoomTabId ||
                    record.TranslationFingerprint != job.ConfigurationFingerprint ||
                    ReadGeneration(connection, transaction, record.RoomTabId) != job.Generation ||
                    (record.Kind == MessageKind.Danmu && filter.IsTranslationBlocked(record.Content)))
                {
                    continue;
                }
                WriteJob(connection, transaction, job with { Priority = TranslationPriority(record) });
                restored++;
            }
            transaction.Commit();
            return restored;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> TrimPendingAsync(DateTimeOffset olderThan, int maxOrdinary, IReadOnlyCollection<string> inFlightJobIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxOrdinary);
        var inFlight = JsonSerializer.Serialize(inFlightJobIds);
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var trimmed = Execute(connection, transaction, """
                UPDATE translation_jobs SET status=2 WHERE status=0 AND priority=0 AND created_ms<$cutoff
                    AND id NOT IN (SELECT value FROM json_each($inFlight));
                """, ("$cutoff", olderThan.ToUnixTimeMilliseconds()), ("$inFlight", inFlight));
            trimmed += Execute(connection, transaction, """
                UPDATE translation_jobs SET status=2 WHERE id IN
                (SELECT id FROM translation_jobs WHERE status=0 AND priority=0
                    AND id NOT IN (SELECT value FROM json_each($inFlight))
                    ORDER BY created_ms DESC,id DESC LIMIT -1 OFFSET $limit);
                """, ("$limit", maxOrdinary), ("$inFlight", inFlight));
            transaction.Commit();
            return trimmed;
        }, cancellationToken);
    }

    private bool UpsertTranslationJob(SqliteConnection connection, SqliteTransaction transaction, string id,
        long generation, string fingerprint, string? expectedFingerprint, bool checkExpectedFingerprint,
        bool forceRefresh = false)
    {
        var record = ReadMessage(connection, transaction, id);
        if (record is null || record.Generation != generation ||
            record.Kind is not (MessageKind.Danmu or MessageKind.SuperChat) ||
            ReadGeneration(connection, transaction, record.RoomTabId) != generation ||
            (checkExpectedFingerprint && record.TranslationFingerprint != expectedFingerprint &&
                record.TranslationFingerprint != fingerprint) ||
            (!forceRefresh && record.TranslationFingerprint == fingerprint && record.Translation is not null))
        {
            return false;
        }
        if (!forceRefresh && record.TranslationFingerprint == fingerprint)
        {
            using var pending = Command(connection, transaction, """
                SELECT 1 FROM translation_jobs
                WHERE id=$id AND message_id=$id AND generation=$generation AND fingerprint=$fingerprint AND status=0;
                """, ("$id", id), ("$generation", generation), ("$fingerprint", fingerprint));
            if (pending.ExecuteScalar() is not null)
            {
                return true;
            }
        }
        record = record with { TranslationFingerprint = fingerprint, Translation = null };
        WriteMessage(connection, transaction, record);
        EnsureJob(connection, transaction, record, fingerprint, forceRefresh ? Guid.NewGuid().ToString("N") : "");
        return true;
    }

    private bool ApplyTranslation(SqliteConnection connection, SqliteTransaction transaction,
        string id, long generation, string fingerprint, string translation)
    {
        var record = ReadMessage(connection, transaction, id);
        if (record is null || record.Generation != generation || record.TranslationFingerprint != fingerprint ||
            ReadGeneration(connection, transaction, record.RoomTabId) != generation)
        {
            return false;
        }
        WriteMessage(connection, transaction, record with { Translation = translation });
        return true;
    }

    private static bool MatchesPendingJob(SqliteConnection connection, SqliteTransaction transaction, TranslationJob job)
    {
        using var command = Command(connection, transaction,
            """
            SELECT 1 FROM translation_jobs
            WHERE id=$id AND message_id=$message AND generation=$generation AND fingerprint=$fingerprint AND status=0
                AND COALESCE(json_extract(payload,'$.RefreshId'),'')=$refresh;
            """, ("$id", job.Id), ("$message", job.MessageId), ("$generation", job.Generation),
            ("$fingerprint", job.ConfigurationFingerprint), ("$refresh", job.RefreshId));
        return command.ExecuteScalar() is not null;
    }

    private static void EnsureJob(SqliteConnection connection, SqliteTransaction transaction, MessageRecord record, string fingerprint,
        string refreshId = "")
    {
        var job = new TranslationJob
        {
            Id = record.Id,
            MessageId = record.Id,
            RoomTabId = record.RoomTabId,
            Kind = record.Kind,
            Generation = record.Generation,
            Text = record.Content,
            EmoticonTexts = record.Emoticons.Select(emoticon => emoticon.Text).ToArray(),
            Priority = TranslationPriority(record),
            CreatedAt = DateTimeOffset.UtcNow,
            ConfigurationFingerprint = fingerprint,
            RefreshId = refreshId
        };
        WriteJob(connection, transaction, job);
    }

    private static void UpdateJobPriority(SqliteConnection connection, SqliteTransaction transaction, MessageRecord record)
    {
        using var command = Command(connection, transaction, "SELECT payload FROM translation_jobs WHERE message_id=$id AND status=0;", ("$id", record.Id));
        if (command.ExecuteScalar() is string payload)
        {
            var job = JsonSerializer.Deserialize<TranslationJob>(payload)!;
            WriteJob(connection, transaction, job with { Priority = TranslationPriority(record) });
        }
    }

    private static int TranslationPriority(MessageRecord record)
    {
        return record.Kind == MessageKind.SuperChat || record.IsPinned ? 1 : 0;
    }

    private static void WriteJob(SqliteConnection connection, SqliteTransaction transaction, TranslationJob job)
    {
        Execute(connection, transaction, """
            INSERT INTO translation_jobs(id,message_id,room_tab_id,generation,fingerprint,priority,created_ms,status,error,payload)
            VALUES($id,$message,$room,$generation,$fingerprint,$priority,$created,0,NULL,$payload)
            ON CONFLICT(id) DO UPDATE SET fingerprint=excluded.fingerprint,priority=excluded.priority,
                created_ms=excluded.created_ms,status=0,error=NULL,payload=excluded.payload;
            """, ("$id", job.Id), ("$message", job.MessageId), ("$room", job.RoomTabId), ("$generation", job.Generation),
            ("$fingerprint", job.ConfigurationFingerprint), ("$priority", job.Priority),
            ("$created", job.CreatedAt.ToUnixTimeMilliseconds()), ("$payload", JsonSerializer.Serialize(job)));
    }

    private static List<TranslationJob> ReadJobs(SqliteCommand command)
    {
        var jobs = new List<TranslationJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(JsonSerializer.Deserialize<TranslationJob>(reader.GetString(0))!);
        }
        return jobs;
    }
}
