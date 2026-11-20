using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证翻译缓存跨退出和启动的顺序、时间及文件生命周期。</summary>
public sealed class TranslationCacheSnapshotTests
{
    /// <summary>通过正常退出写盘，重启时按原 TTL 恢复译文与最近使用顺序。</summary>
    [Fact]
    public async Task RestartRestoresRecencyAndOriginalExpiration()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = Path.Combine(directory.Path, "translation-cache.json");
        var time = new TranslationCacheTestTimeProvider();
        var failures = new List<Exception>();
        var first = Request("first");
        var second = Request("second");
        var third = Request("third");
        var firstStoredAt = time.GetUtcNow();
        TranslationCacheSnapshotStore snapshot;
        await using (var service = Service("cached", time, capacity: 2, ttlMinutes: 5))
        {
            snapshot = new(path, service, failures.Add);
            await snapshot.RestoreAsync();
            Assert.Equal("cached first", await service.TranslateAsync(first));
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("cached second", await service.TranslateAsync(second));
            Assert.Equal("cached first", await service.TranslateAsync(first));
            Assert.False(File.Exists(path));
        }

        await snapshot.SaveAsync();
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
        {
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("Version").GetInt32());
            var entries = root.GetProperty("Entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.Equal(second.CacheKey, entries[0].GetProperty("Key").GetString());
            Assert.Equal(first.CacheKey, entries[1].GetProperty("Key").GetString());
            Assert.Equal(firstStoredAt, entries[1].GetProperty("StoredAtUtc").GetDateTimeOffset());
        }
        Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.Path)));

        time.Advance(TimeSpan.FromMinutes(1));
        await using var reopened = Service("new", time, capacity: 2, ttlMinutes: 5);
        await new TranslationCacheSnapshotStore(path, reopened, failures.Add).RestoreAsync();
        Assert.Equal("new third", await reopened.TranslateAsync(third));
        Assert.Equal("cached first", await reopened.TranslateAsync(first));
        Assert.Equal("new second", await reopened.TranslateAsync(second));

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("new first", await reopened.TranslateAsync(first));
        Assert.Equal("new second", await reopened.TranslateAsync(second));
        Assert.Empty(failures);
    }

    /// <summary>启动时使用当前的 TTL 和容量，先排除过期项再保留新近项。</summary>
    [Fact]
    public async Task RestorePrunesExpiredEntriesBeforeApplyingTheCurrentCapacity()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = Path.Combine(directory.Path, "translation-cache.json");
        var failures = new List<Exception>();
        var time = new TranslationCacheTestTimeProvider();
        var expired = Request("expired");
        var recent = Request("recent");
        var trimmed = Request("trimmed");
        var kept = Request("kept");
        await using (var previous = Service("cached", time, capacity: 4, ttlMinutes: 10))
        {
            var snapshot = new TranslationCacheSnapshotStore(path, previous, failures.Add);
            await snapshot.RestoreAsync();
            await previous.TranslateAsync(expired);
            time.Advance(TimeSpan.FromMinutes(3));
            await previous.TranslateAsync(recent);
            await previous.TranslateAsync(trimmed);
            await previous.TranslateAsync(kept);
            await previous.TranslateAsync(recent);
            await previous.TranslateAsync(expired);
            await snapshot.SaveAsync();
        }

        time.Advance(TimeSpan.FromMinutes(3));
        await using var reopened = Service("new", time, capacity: 2, ttlMinutes: 5);
        await new TranslationCacheSnapshotStore(path, reopened, failures.Add).RestoreAsync();

        Assert.Equal("cached kept", await reopened.TranslateAsync(kept));
        Assert.Equal("cached recent", await reopened.TranslateAsync(recent));
        Assert.Equal("new expired", await reopened.TranslateAsync(expired));
        Assert.Equal("new trimmed", await reopened.TranslateAsync(trimmed));
        Assert.Empty(failures);
    }

    /// <summary>损坏的快照记录错误，翻译继续使用模型。</summary>
    [Fact]
    public async Task CorruptSnapshotIsReportedAndTranslationContinues()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "translation-cache.json");
        await File.WriteAllTextAsync(path, "{\"Version\":1,\"Entries\":[");
        await using var service = Service("new", new TranslationCacheTestTimeProvider());
        var failures = new List<Exception>();

        await new TranslationCacheSnapshotStore(path, service, failures.Add).RestoreAsync();

        Assert.Single(failures);
        Assert.Equal("new hello", await service.TranslateAsync(Request("hello")));
    }

    /// <summary>读取到更新版本的快照时，当前会话可使用翻译且关闭时保留文件。</summary>
    [Fact]
    public async Task UnsupportedSnapshotVersionIsPreservedWhenClosing()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "translation-cache.json");
        const string NEWER_SNAPSHOT = """{"Version":2,"Entries":[]}""";
        await File.WriteAllTextAsync(path, NEWER_SNAPSHOT);
        await using var service = Service("new", new TranslationCacheTestTimeProvider());
        var snapshot = new TranslationCacheSnapshotStore(path, service, _ => { });

        await snapshot.RestoreAsync();
        Assert.Equal("new hello", await service.TranslateAsync(Request("hello")));
        await snapshot.SaveAsync();

        Assert.Equal(NEWER_SNAPSHOT, await File.ReadAllTextAsync(path));
    }

    /// <summary>清理立即删除磁盘快照，下一次启动重新翻译。</summary>
    [Fact]
    public async Task ClearingDeletesTheSnapshotBeforeRestart()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = Path.Combine(directory.Path, "translation-cache.json");
        var failures = new List<Exception>();
        var time = new TranslationCacheTestTimeProvider();
        var request = Request("hello");
        await using (var previous = Service("cached", time))
        {
            var snapshot = new TranslationCacheSnapshotStore(path, previous, failures.Add);
            await snapshot.RestoreAsync();
            await previous.TranslateAsync(request);
            await snapshot.SaveAsync();
            Assert.True(File.Exists(path));

            await snapshot.ClearAsync();
            Assert.False(File.Exists(path));
        }

        await using var reopened = Service("new", time);
        await new TranslationCacheSnapshotStore(path, reopened, failures.Add).RestoreAsync();

        Assert.Equal("new hello", await reopened.TranslateAsync(request));
        Assert.Empty(failures);
    }

    /// <summary>启动中止在恢复前或恢复期间，关闭不会覆盖上次正常写盘的数据。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingWithoutACompletedRestorePreservesThePriorSnapshot(bool cancelRestore)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "translation-cache.json");
        const string ORIGINAL_SNAPSHOT = """{"Version":1,"Entries":[]}""";
        await File.WriteAllTextAsync(path, ORIGINAL_SNAPSHOT);
        await using var service = Service("new", new TranslationCacheTestTimeProvider());
        var failures = new List<Exception>();
        var snapshot = new TranslationCacheSnapshotStore(path, service, failures.Add);
        if (cancelRestore)
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot.RestoreAsync(cancellation.Token));
        }

        await snapshot.SaveAsync();

        Assert.Equal(ORIGINAL_SNAPSHOT, await File.ReadAllTextAsync(path));
        Assert.Empty(failures);
    }

    private static TranslationRequest Request(string text)
    {
        return TranslationRequestFactory.Create(new TranslationSettings(), text)!;
    }

    private static TranslationService Service(string prefix, TimeProvider timeProvider, int capacity = 2, int ttlMinutes = 5)
    {
        return new(new DelegateTranslationClient((request, _) => Task.FromResult($"{prefix} {request.Text}")),
            capacity, ttlMinutes, timeProvider);
    }
}
