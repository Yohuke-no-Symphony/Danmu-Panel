using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed record FeedWindowProjection(
    IReadOnlyList<FeedRowUpdate> Updates,
    IReadOnlyList<FeedCollectionEdit<object>> Items,
    IReadOnlyList<FeedCollectionEdit<object>> Pinned,
    IReadOnlyList<FeedCollectionEdit<object>> Scrolling,
    IReadOnlyList<FeedCollectionEdit<object>> Rows,
    IReadOnlyList<MessageRecord> TranslationCandidates)
{
    internal bool HasChanges => Updates.Count + Items.Count + Pinned.Count + Scrolling.Count + Rows.Count > 0;
}
