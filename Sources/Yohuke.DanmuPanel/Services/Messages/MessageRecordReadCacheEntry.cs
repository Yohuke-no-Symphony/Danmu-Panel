using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed record MessageRecordReadCacheEntry(string Id, string Payload, MessageRecord Record, long TextBytes)
{
    internal long Sequence { get; set; }
    internal long ValidatedEpoch { get; set; } = -1;
}
