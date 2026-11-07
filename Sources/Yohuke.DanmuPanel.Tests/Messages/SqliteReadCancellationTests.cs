using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证运行中的纯 SQLite 读取可取消，且取消状态不会污染后续事务。</summary>
public sealed class SqliteReadCancellationTests
{
    /// <summary>无需可见性回调的长查询也可中断，释放取消作用域后同一连接恢复正常。</summary>
    [Fact]
    public async Task CancellationInterruptsNativeQueryAndIsRemovedBeforeNextTransaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.CreateFunction("read_started", () =>
        {
            started.TrySetResult();
            return 0;
        });
        using (var cancelledTransaction = connection.BeginTransaction(deferred: true))
        {
            using var readCancellation = new SqliteReadCancellation(connection, cancellation.Token);
            using var command = connection.CreateCommand();
            command.Transaction = cancelledTransaction;
            command.CommandText = """
                WITH RECURSIVE numbers(value) AS (
                    VALUES(read_started()) UNION ALL SELECT value+1 FROM numbers WHERE value<5000000)
                SELECT SUM(value) FROM numbers;
                """;
            var reading = Task.Run(() => command.ExecuteScalar());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

            var error = await Assert.ThrowsAsync<SqliteException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(9, error.SqliteErrorCode);
        }
        using var transaction = connection.BeginTransaction();
        using var next = connection.CreateCommand();
        next.Transaction = transaction;
        next.CommandText = "CREATE TABLE committed(value INTEGER); INSERT INTO committed VALUES(42); SELECT value FROM committed;";

        Assert.Equal(42L, next.ExecuteScalar());
        transaction.Commit();
    }
}
