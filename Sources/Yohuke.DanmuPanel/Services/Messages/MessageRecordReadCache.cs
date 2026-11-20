using System.Text.Json;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed class MessageRecordReadCache
{
    private const int DEFAULT_CAPACITY = 16384;
    private const long DEFAULT_TEXT_BYTE_CAPACITY = 32L * 1024 * 1024;
    private readonly int capacity;
    private readonly long textByteCapacity;
    private readonly Dictionary<string, LinkedListNode<MessageRecordReadCacheEntry>> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<long, LinkedListNode<MessageRecordReadCacheEntry>> sequences = new();
    private readonly LinkedList<MessageRecordReadCacheEntry> recency = new();

    internal int Count => entries.Count;
    internal long RetainedTextBytes { get; private set; }

    internal MessageRecordReadCache(int capacity = DEFAULT_CAPACITY, long textByteCapacity = DEFAULT_TEXT_BYTE_CAPACITY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textByteCapacity);
        this.capacity = capacity;
        this.textByteCapacity = textByteCapacity;
    }

    internal MessageRecord GetOrDeserialize(string id, string payload)
    {
        if (entries.TryGetValue(id, out var previous))
        {
            if (string.Equals(previous.Value.Payload, payload, StringComparison.Ordinal))
            {
                recency.Remove(previous);
                recency.AddLast(previous);
                return previous.Value.Record;
            }
            Remove(previous);
        }

        var record = JsonSerializer.Deserialize<MessageRecord>(payload) ?? throw new JsonException("A message payload cannot be null.");
        record = record with
        {
            Emoticons = record.Emoticons.Count == 0 ? Array.Empty<MessageEmoticon>() : Array.AsReadOnly(record.Emoticons.ToArray()),
            RedPocket = record.RedPocket?.CreateSnapshot()
        };
        var textBytes = ((long)id.Length + payload.Length) * sizeof(char);
        if (textBytes > textByteCapacity)
        {
            return record;
        }
        while (entries.Count >= capacity || RetainedTextBytes > textByteCapacity - textBytes)
        {
            Remove(recency.First!);
        }
        entries.Add(id, recency.AddLast(new MessageRecordReadCacheEntry(id, payload, record, textBytes)));
        RetainedTextBytes += textBytes;
        return record;
    }

    internal bool IsValidated(long sequence, long payloadRevision)
    {
        return sequences.TryGetValue(sequence, out var node) && node.Value.PayloadRevision == payloadRevision;
    }

    internal bool TryGetValidated(long sequence, long payloadRevision, out MessageRecordReadCacheEntry entry)
    {
        if (sequences.TryGetValue(sequence, out var node) && node.Value.PayloadRevision == payloadRevision)
        {
            recency.Remove(node);
            recency.AddLast(node);
            entry = node.Value;
            return true;
        }
        entry = null!;
        return false;
    }

    internal MessageRecord GetOrDeserialize(long sequence, long payloadRevision, string id, string payload)
    {
        if (sequences.TryGetValue(sequence, out var previousSequence) && previousSequence.Value.Id != id)
        {
            Remove(previousSequence);
        }
        if (entries.TryGetValue(id, out var previousId) && previousId.Value.Sequence != 0 &&
            previousId.Value.Sequence != sequence)
        {
            Remove(previousId);
        }
        var record = GetOrDeserialize(id, payload);
        if (entries.TryGetValue(id, out var current))
        {
            current.Value.Sequence = sequence;
            current.Value.PayloadRevision = payloadRevision;
            sequences[sequence] = current;
        }
        return record;
    }

    internal void Clear()
    {
        entries.Clear();
        sequences.Clear();
        recency.Clear();
        RetainedTextBytes = 0;
    }

    private void Remove(LinkedListNode<MessageRecordReadCacheEntry> entry)
    {
        entries.Remove(entry.Value.Id);
        if (entry.Value.Sequence != 0)
        {
            sequences.Remove(entry.Value.Sequence);
        }
        recency.Remove(entry);
        RetainedTextBytes -= entry.Value.TextBytes;
    }
}
