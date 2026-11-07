using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>约束持久 LRU 的重复命中写入量和重开后的淘汰规则。</summary>
public sealed class SqliteTranslationCacheWriteTests
{
    /// <summary>命中已经最新的项不写入；较旧项只提升一次，提升顺序重开后仍生效。</summary>
    [Fact]
    public async Task RepeatedMostRecentHitsDoNotWriteAndPromotionsSurviveReopen()
    {
        using var directory = new MessageStoreTestDirectory();
        await using (var cache = new SqliteTranslationCache(directory.DatabasePath, 2))
        {
            await cache.SetAsync("a", "A");
            await cache.SetAsync("b", "B");
            using var connection = Open(directory.DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE writes (amount INTEGER NOT NULL);
                INSERT INTO writes VALUES(0);
                CREATE TRIGGER count_translation_update AFTER UPDATE ON translations
                BEGIN UPDATE writes SET amount=amount+1; END;
                """;
            command.ExecuteNonQuery();

            for (var index = 0; index < 100; index++)
            {
                Assert.Equal("B", await cache.GetAsync("b"));
            }
            command.CommandText = "SELECT amount FROM writes;";
            Assert.Equal(0L, command.ExecuteScalar());
            Assert.Equal("A", await cache.GetAsync("a"));
            for (var index = 0; index < 100; index++)
            {
                Assert.Equal("A", await cache.GetAsync("a"));
            }
            Assert.Equal(1L, command.ExecuteScalar());
        }
        await using var reopened = new SqliteTranslationCache(directory.DatabasePath, 2);
        Assert.Equal("A", await reopened.GetAsync("a"));
        await reopened.SetAsync("c", "C");

        Assert.Null(await reopened.GetAsync("b"));
        Assert.Equal("A", await reopened.GetAsync("a"));
        Assert.Equal("C", await reopened.GetAsync("c"));
    }

    /// <summary>以更小容量重开时，更新现有项也会淘汰超额的最旧项。</summary>
    [Fact]
    public async Task UpdatingAfterReopeningAtSmallerCapacityRemovesOnlyOldestEntries()
    {
        using var directory = new MessageStoreTestDirectory();
        await using (var cache = new SqliteTranslationCache(directory.DatabasePath, 4))
        {
            foreach (var key in new[] { "a", "b", "c", "d" })
            {
                await cache.SetAsync(key, key);
            }
        }
        await using var reopened = new SqliteTranslationCache(directory.DatabasePath, 2);

        await reopened.SetAsync("c", "changed");

        Assert.Null(await reopened.GetAsync("a"));
        Assert.Null(await reopened.GetAsync("b"));
        Assert.Equal("changed", await reopened.GetAsync("c"));
        Assert.Equal("d", await reopened.GetAsync("d"));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
