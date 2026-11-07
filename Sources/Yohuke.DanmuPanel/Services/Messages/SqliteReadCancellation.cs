using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed class SqliteReadCancellation : IDisposable
{
    private const int PROGRESS_INSTRUCTIONS = 1000;
    private readonly SqliteConnection connection;
    private bool isRegistered;

    internal SqliteReadCancellation(SqliteConnection connection, CancellationToken cancellationToken)
    {
        this.connection = connection;
        if (cancellationToken.CanBeCanceled)
        {
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, PROGRESS_INSTRUCTIONS,
                static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, cancellationToken);
            isRegistered = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (isRegistered)
        {
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            isRegistered = false;
        }
    }
}
