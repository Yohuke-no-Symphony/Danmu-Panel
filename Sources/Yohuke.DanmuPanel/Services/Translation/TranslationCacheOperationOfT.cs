using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed class TranslationCacheOperationOfT<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken)
    : TranslationCacheOperation
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
