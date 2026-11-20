namespace Yohuke.DanmuPanel.Models.Messages;

internal sealed record MessageFeedWindowNavigation(
    MessageFeedPageRequest Scrolling,
    MessageFeedPageRequest? Pinned,
    int ScrollingCapacity,
    int ScrollingFallbackLimit,
    int PageSize,
    MessageFeedPageDirection? ScrollingDirection = null,
    MessageFeedPageRequest? RetainedScrolling = null,
    IReadOnlyList<MessageFeedEntry>? PreviousScrolling = null,
    MessageFeedPageRequest? EmptyPinnedFallback = null,
    (string FirstId, string LastId, bool Preserve)? LiveScrollingAnchors = null,
    (string AnchorId, string FirstId, string LastId, int WindowSize)? ReadingAnchor = null);
