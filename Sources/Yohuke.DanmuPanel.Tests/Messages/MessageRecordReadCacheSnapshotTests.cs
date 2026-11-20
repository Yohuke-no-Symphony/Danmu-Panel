using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证 SQLite 行版本、行身份和缓存淘汰同步更新。</summary>
public sealed class MessageRecordReadCacheSnapshotTests
{
    /// <summary>只有当前行的版本变化需要重新校验，相同正文可继续复用原对象。</summary>
    [Fact]
    public void ChangedRevisionRevalidatesOnlyTheChangedRow()
    {
        var cache = new MessageRecordReadCache();
        var payload = Payload("message");
        var original = cache.GetOrDeserialize(1, 2, "message", payload);
        var other = cache.GetOrDeserialize(2, 2, "other", Payload("other"));

        Assert.True(cache.TryGetValidated(1, 2, out var entry));
        Assert.Same(original, entry.Record);
        Assert.False(cache.IsValidated(1, 3));
        Assert.True(cache.TryGetValidated(2, 2, out var unaffected));
        Assert.Same(other, unaffected.Record);
        Assert.Same(original, cache.GetOrDeserialize(1, 3, "message", payload));
        Assert.True(cache.IsValidated(1, 3));
        Assert.False(cache.IsValidated(1, 2));
    }

    /// <summary>相同行 ID 被重新插入新 sequence 时，即使正文相同也属于新行。</summary>
    [Fact]
    public void AReinsertedIdDoesNotRetainThePreviousSequenceIdentity()
    {
        var cache = new MessageRecordReadCache();
        var payload = Payload("message");
        var original = cache.GetOrDeserialize(1, 0, "message", payload);

        var reinserted = cache.GetOrDeserialize(2, 0, "message", payload);

        Assert.NotSame(original, reinserted);
        Assert.False(cache.IsValidated(1, 0));
        Assert.True(cache.IsValidated(2, 0));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>LRU 淘汰同时移除 sequence 映射，命中不会返回已淘汰正文。</summary>
    [Fact]
    public void EvictionRemovesTheSequenceIndex()
    {
        var cache = new MessageRecordReadCache(2);
        cache.GetOrDeserialize(1, 0, "first", Payload("first"));
        cache.GetOrDeserialize(2, 0, "second", Payload("second"));
        Assert.True(cache.TryGetValidated(1, 0, out _));

        cache.GetOrDeserialize(3, 0, "third", Payload("third"));

        Assert.True(cache.IsValidated(1, 0));
        Assert.False(cache.IsValidated(2, 0));
        Assert.True(cache.IsValidated(3, 0));
        Assert.Equal(2, cache.Count);
    }

    private static string Payload(string id)
    {
        return JsonSerializer.Serialize(new MessageRecord { Id = id, RoomTabId = "room", Content = "正文" });
    }
}
