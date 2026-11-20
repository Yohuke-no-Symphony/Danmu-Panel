namespace Yohuke.DanmuPanel.Models.Messages;

internal sealed record MessageFeedPageRequest(int Limit, MessageFeedPageDirection Direction = MessageFeedPageDirection.Latest,
    MessageFeedCursor? Boundary = null, MessageFeedCursor? Through = null, string? TargetId = null);
