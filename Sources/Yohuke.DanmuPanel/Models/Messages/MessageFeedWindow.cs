namespace Yohuke.DanmuPanel.Models.Messages;

internal sealed record MessageFeedWindow(MessageFeedPage Scrolling, MessageFeedPage Pinned, int UnreadCount,
    IReadOnlySet<string> PendingTranslationIds);
