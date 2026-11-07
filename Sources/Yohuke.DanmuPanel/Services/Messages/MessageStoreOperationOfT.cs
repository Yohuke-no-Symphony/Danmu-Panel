using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed class MessageStoreOperationOfT<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken)
    : MessageStoreOperation
{
    private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<T> Task => completion.Task;

    internal override void Execute(SqliteConnection connection)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return;
        }

        try
        {
            completion.TrySetResult(operation(connection));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    internal override void Fail(Exception error)
    {
        completion.TrySetException(error);
    }
}
