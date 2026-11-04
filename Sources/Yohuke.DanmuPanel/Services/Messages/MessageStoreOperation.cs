using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Messages;

internal abstract class MessageStoreOperation
{
    internal abstract void Execute(SqliteConnection connection);
    internal abstract void Fail(Exception error);
}
