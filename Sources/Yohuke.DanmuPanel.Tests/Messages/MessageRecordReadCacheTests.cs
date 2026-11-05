using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证消息读取缓存的 LRU 淘汰、文本预算以及 payload 校验。</summary>
public sealed class MessageRecordReadCacheTests
{
    /// <summary>不同 SQL 行 ID 即使具有相同 payload，也不能共享另一行的缓存项。</summary>
    [Fact]
    public void DifferentRowIdsWithIdenticalPayloadKeepSeparateEntries()
    {
        var cache = new MessageRecordReadCache();
        var payload = Payload("payload-id", "same payload");
        var first = cache.GetOrDeserialize("first-row", payload);

        var second = cache.GetOrDeserialize("second-row", payload);

        Assert.Equal(2, cache.Count);
        Assert.NotSame(first, second);
        Assert.Same(first, cache.GetOrDeserialize("first-row", payload));
        Assert.Same(second, cache.GetOrDeserialize("second-row", payload));
    }

    /// <summary>条数达到上限时淘汰最久未使用的记录，命中会更新保留顺序。</summary>
    [Fact]
    public void CapacityEvictsTheLeastRecentlyReadRecord()
    {
        var cache = new MessageRecordReadCache(3);
        var firstPayload = Payload("first", "one");
        var secondPayload = Payload("second", "two");
        var first = cache.GetOrDeserialize("first", firstPayload);
        var second = cache.GetOrDeserialize("second", secondPayload);
        cache.GetOrDeserialize("third", Payload("third", "three"));
        Assert.Same(first, cache.GetOrDeserialize("first", firstPayload));

        cache.GetOrDeserialize("fourth", Payload("fourth", "four"));

        Assert.Equal(3, cache.Count);
        Assert.Same(first, cache.GetOrDeserialize("first", firstPayload));
        Assert.NotSame(second, cache.GetOrDeserialize("second", secondPayload));
        Assert.Equal(3, cache.Count);
    }

    /// <summary>文本预算独立于条数上限生效，并按 UTF-16 文本字节计入 ID 与 payload。</summary>
    [Fact]
    public void TextBudgetEvictsBeforeTheEntryCapacityIsReached()
    {
        var firstPayload = Payload("a", "一");
        var secondPayload = Payload("b", "二");
        var budget = ((long)firstPayload.Length + 1) * sizeof(char);
        var cache = new MessageRecordReadCache(100, budget);
        var first = cache.GetOrDeserialize("a", firstPayload);

        cache.GetOrDeserialize("b", secondPayload);

        Assert.Equal(1, cache.Count);
        Assert.InRange(cache.RetainedTextBytes, 1, budget);
        Assert.NotSame(first, cache.GetOrDeserialize("a", firstPayload));
    }

    /// <summary>超出预算的单条仍可正常读取，但不会替换其他缓存或被持续保留。</summary>
    [Fact]
    public void OversizedPayloadIsParsedWithoutBeingRetained()
    {
        var smallPayload = Payload("small", "one");
        var budget = ((long)smallPayload.Length + "small".Length) * sizeof(char);
        var cache = new MessageRecordReadCache(100, budget);
        var small = cache.GetOrDeserialize("small", smallPayload);
        var oversizedPayload = Payload("large", new string('x', 10000));

        var first = cache.GetOrDeserialize("large", oversizedPayload);
        var second = cache.GetOrDeserialize("large", oversizedPayload);

        Assert.Equal(new string('x', 10000), first.Content);
        Assert.NotSame(first, second);
        Assert.Same(small, cache.GetOrDeserialize("small", smallPayload));
        Assert.Equal(1, cache.Count);
        Assert.Equal(budget, cache.RetainedTextBytes);
    }

    /// <summary>相同 ID 的 payload 变化替换旧记录，保留文本字节数不会累计旧版本。</summary>
    [Fact]
    public void ReplacingPayloadUpdatesTheRetainedByteCount()
    {
        var cache = new MessageRecordReadCache();
        var original = cache.GetOrDeserialize("message", Payload("message", "one"));
        var updatedPayload = Payload("message", "a longer replacement");

        var updated = cache.GetOrDeserialize("message", updatedPayload);

        Assert.NotSame(original, updated);
        Assert.Equal("a longer replacement", updated.Content);
        Assert.Equal(1, cache.Count);
        Assert.Equal(((long)updatedPayload.Length + "message".Length) * sizeof(char), cache.RetainedTextBytes);
        Assert.Same(updated, cache.GetOrDeserialize("message", updatedPayload));
    }

    /// <summary>相同 ID 被替换为超大文本时旧版本释放，错误 payload 也不能返回旧值。</summary>
    [Fact]
    public void OversizedOrInvalidReplacementCannotLeaveThePreviousRecordCached()
    {
        var payload = Payload("message", "one");
        var budget = ((long)payload.Length + "message".Length) * sizeof(char);
        var cache = new MessageRecordReadCache(10, budget);
        cache.GetOrDeserialize("message", payload);

        cache.GetOrDeserialize("message", Payload("message", new string('x', 10000)));

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedTextBytes);
        cache.GetOrDeserialize("message", payload);
        Assert.Throws<JsonException>(() => cache.GetOrDeserialize("message", "{"));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedTextBytes);
    }

    /// <summary>释放缓存移除全部记录及文本预算占用，调用方已取得的只读记录仍可使用。</summary>
    [Fact]
    public void ClearingReleasesRetainedEntriesAndText()
    {
        var cache = new MessageRecordReadCache();
        var payload = Payload("message", "one");
        var record = cache.GetOrDeserialize("message", payload);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedTextBytes);
        Assert.Equal("one", record.Content);
        Assert.NotSame(record, cache.GetOrDeserialize("message", payload));
    }

    private static string Payload(string id, string content)
    {
        return JsonSerializer.Serialize(new MessageRecord
        {
            Id = id, Content = content, Time = DateTimeOffset.FromUnixTimeSeconds(1800000000)
        });
    }
}
