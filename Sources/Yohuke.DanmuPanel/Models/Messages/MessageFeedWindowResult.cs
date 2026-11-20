namespace Yohuke.DanmuPanel.Models.Messages;

internal readonly record struct MessageFeedWindowResult(
    MessageFeedWindow Window,
    MessageFeedPageRequest ScrollingRequest,
    MessageFeedPageRequest? PinnedRequest,
    bool ReadingWindowPreserved = false);
