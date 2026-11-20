using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private static readonly TimeSpan CHECKPOINT_INTERVAL = TimeSpan.FromSeconds(1);
    private readonly TaskCompletionSource stopCheckpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long writerOperations;
    private bool checkpointAvailable;

    private async Task CheckpointAsync(string path)
    {
        try
        {
            await ready.Task.ConfigureAwait(false);
            if (stopCheckpoint.Task.IsCompleted)
            {
                return;
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString());
            connection.Open();
            Execute(connection, null, "PRAGMA synchronous=FULL;");
            Volatile.Write(ref checkpointAvailable, true);
            var observedOperations = 0L;
            var incomplete = false;
            while (!stopCheckpoint.Task.IsCompleted)
            {
                var interval = Task.Delay(CHECKPOINT_INTERVAL);
                if (await Task.WhenAny(interval, stopCheckpoint.Task).ConfigureAwait(false) != interval)
                {
                    break;
                }
                var operations = Interlocked.Read(ref writerOperations);
                if (operations == observedOperations && !incomplete)
                {
                    continue;
                }
                using var command = Command(connection, null, "PRAGMA wal_checkpoint(PASSIVE);");
                using var reader = command.ExecuteReader();
                incomplete = reader.Read() && (reader.GetInt32(0) != 0 || reader.GetInt32(1) > reader.GetInt32(2));
                observedOperations = operations;
            }
        }
        catch (SqliteException)
        {
            Volatile.Write(ref checkpointAvailable, false);
        }
        catch (Exception) when (ready.Task.IsFaulted)
        {
        }
        finally
        {
            Volatile.Write(ref checkpointAvailable, false);
        }
    }
}
