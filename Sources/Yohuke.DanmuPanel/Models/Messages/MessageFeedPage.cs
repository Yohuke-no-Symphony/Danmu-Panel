namespace Yohuke.DanmuPanel.Models.Messages;

internal sealed record MessageFeedPage(IReadOnlyList<MessageFeedEntry> Entries, bool HasOlder, bool HasNewer);
