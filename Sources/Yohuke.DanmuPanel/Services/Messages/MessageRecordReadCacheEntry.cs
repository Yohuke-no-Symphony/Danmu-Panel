using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed record MessageRecordReadCacheEntry(string Id, string Payload, MessageRecord Record, long TextBytes);
