using Xunit;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class SqliteTranslationCacheTests
{
    [Fact]
    public async Task EvictsLeastRecentlyUsedAndSurvivesReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "translations.db");
        try
        {
            await using (var cache = new SqliteTranslationCache(path, 2))
            {
                await cache.SetAsync("a", "A");
                await cache.SetAsync("b", "B");
                Assert.Equal("A", await cache.GetAsync("a"));
                await cache.SetAsync("c", "C");
                Assert.Null(await cache.GetAsync("b"));
            }

            await using (var cache = new SqliteTranslationCache(path, 2))
            {
                Assert.Equal("A", await cache.GetAsync("a"));
                Assert.Equal("C", await cache.GetAsync("c"));
                await cache.ClearAsync();
                Assert.Null(await cache.GetAsync("a"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
