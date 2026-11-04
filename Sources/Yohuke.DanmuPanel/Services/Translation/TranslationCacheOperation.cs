using Microsoft.Data.Sqlite;

namespace Yohuke.DanmuPanel.Services.Translation;

internal abstract class TranslationCacheOperation
{
    internal abstract void Execute(SqliteConnection connection);
    internal abstract void Fail(Exception error);
}
