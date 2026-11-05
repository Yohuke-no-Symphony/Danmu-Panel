using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证 SQLite 快照验证代次、行身份和缓存淘汰同步更新。</summary>
public sealed class MessageRecordReadCacheSnapshotTests
{
    /// <summary>外部提交只撤销有效性，重新校验相同正文仍复用原对象。</summary>
    [Fact]
    public void ANewSnapshotRevalidatesWithoutDiscardingUnchangedParsedRecords()
    {
        var cache = new MessageRecordReadCache();
        var payload = Payload("message", "room");
        cache.BeginSnapshot(2);
        var original = cache.GetOrDeserialize(1, "message", payload);
        Assert.True(cache.TryGetValidated(1, out var entry));
        Assert.Same(original, entry.Record);

        cache.BeginSnapshot(3);

        Assert.False(cache.IsValidated(1));
        Assert.Same(original, cache.GetOrDeserialize(1, "message", payload));
        Assert.True(cache.IsValidated(1));
        cache.Invalidate("message");
        Assert.False(cache.IsValidated(1));
        Assert.Same(original, cache.GetOrDeserialize(1, "message", payload));
    }

    /// <summary>相同行 ID 被重新插入新 sequence 时，即使正文相同也属于新行。</summary>
    [Fact]
    public void AReinsertedIdDoesNotRetainThePreviousSequenceIdentity()
    {
        var cache = new MessageRecordReadCache();
        var payload = Payload("message", "room");
        cache.BeginSnapshot(2);
        var original = cache.GetOrDeserialize(1, "message", payload);

        var reinserted = cache.GetOrDeserialize(2, "message", payload);

        Assert.NotSame(original, reinserted);
        Assert.False(cache.IsValidated(1));
        Assert.True(cache.IsValidated(2));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>LRU 淘汰同时移除 sequence 映射，命中不会返回已淘汰正文。</summary>
    [Fact]
    public void EvictionRemovesTheSequenceIndex()
    {
        var cache = new MessageRecordReadCache(2);
        cache.BeginSnapshot(2);
        cache.GetOrDeserialize(1, "first", Payload("first", "room"));
        cache.GetOrDeserialize(2, "second", Payload("second", "room"));
        Assert.True(cache.TryGetValidated(1, out _));

        cache.GetOrDeserialize(3, "third", Payload("third", "room"));

        Assert.True(cache.IsValidated(1));
        Assert.False(cache.IsValidated(2));
        Assert.True(cache.IsValidated(3));
        Assert.Equal(2, cache.Count);
    }

    /// <summary>清空指定房间与类型只删除该范围的正文和 sequence 映射。</summary>
    [Fact]
    public void ClearingAFeedDoesNotDiscardOtherRoomsOrKinds()
    {
        var cache = new MessageRecordReadCache();
        cache.BeginSnapshot(2);
        cache.GetOrDeserialize(1, "danmu", Payload("danmu", "room"));
        cache.GetOrDeserialize(2, "gift", Payload("gift", "room", MessageKind.Gift));
        cache.GetOrDeserialize(3, "other", Payload("other", "other"));

        cache.InvalidateRoom("room", MessageKind.Danmu);

        Assert.False(cache.IsValidated(1));
        Assert.True(cache.IsValidated(2));
        Assert.True(cache.IsValidated(3));
        cache.InvalidateRoom("room");
        Assert.False(cache.IsValidated(2));
        Assert.Equal(1, cache.Count);
    }

    private static string Payload(string id, string room, MessageKind kind = MessageKind.Danmu)
    {
        return JsonSerializer.Serialize(new MessageRecord { Id = id, RoomTabId = room, Kind = kind, Content = "正文" });
    }
}
