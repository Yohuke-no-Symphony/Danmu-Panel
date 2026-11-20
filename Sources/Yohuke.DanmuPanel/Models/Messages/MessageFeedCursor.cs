namespace Yohuke.DanmuPanel.Models.Messages;

internal readonly record struct MessageFeedCursor(long OccurredMilliseconds, long Sequence);
