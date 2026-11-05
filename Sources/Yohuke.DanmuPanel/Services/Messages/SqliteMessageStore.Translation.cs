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
            var record = ReadMessage(connection, transaction, id);
            if (record is null || record.Generation != generation ||
                record.Kind is not (MessageKind.Danmu or MessageKind.SuperChat) ||
                (record.TranslationFingerprint == fingerprint && record.Translation is not null))
            {
                return false;
            }
            record = record with { TranslationFingerprint = fingerprint, Translation = null };
            WriteMessage(connection, transaction, record);
            EnsureJob(connection, transaction, record, fingerprint);
            transaction.Commit();
            return true;
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
            WHERE id=$id AND generation=$generation AND fingerprint=$fingerprint AND status=0;
            """, ("$error", error), ("$id", job.Id), ("$generation", job.Generation),
            ("$fingerprint", job.ConfigurationFingerprint)) > 0, cancellationToken);
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

    private static bool ApplyTranslation(SqliteConnection connection, SqliteTransaction transaction,
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
            "SELECT 1 FROM translation_jobs WHERE id=$id AND message_id=$message AND generation=$generation AND fingerprint=$fingerprint AND status=0;",
            ("$id", job.Id), ("$message", job.MessageId), ("$generation", job.Generation), ("$fingerprint", job.ConfigurationFingerprint));
        return command.ExecuteScalar() is not null;
    }

    private static void EnsureJob(SqliteConnection connection, SqliteTransaction transaction, MessageRecord record, string fingerprint)
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
            ConfigurationFingerprint = fingerprint
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
