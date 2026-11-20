using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

internal readonly record struct SqliteFeedRow(long Sequence, long OccurredMilliseconds, long PayloadRevision)
{
    internal MessageFeedCursor Cursor => new(OccurredMilliseconds, Sequence);
}
