using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private const int LOADING_HINT_ROW_COUNT = 3;

    private void AddLoadingRows(bool newer)
    {
        var prepend = SortOrder == MessageSortOrder.NewestFirst ? newer : !newer;
        var index = prepend ? 0 : ScrollingRows.Count;
        var placeholders = Enumerable.Range(0, LOADING_HINT_ROW_COUNT)
            .Select(_ => (FeedRowViewModel)new MessageLoadingPlaceholderViewModel()).ToArray();
        ((FeedCollection<FeedRowViewModel>)ScrollingRows).InsertRange(index, placeholders);
        NotifyRegions();
    }

    private void RemoveLoadingRows()
    {
        for (var index = ScrollingRows.Count - 1; index >= 0; index--)
        {
            if (ScrollingRows[index] is not MessageLoadingPlaceholderViewModel)
            {
                continue;
            }
            var first = index;
            while (first > 0 && ScrollingRows[first - 1] is MessageLoadingPlaceholderViewModel)
            {
                first--;
            }
            ((FeedCollection<FeedRowViewModel>)ScrollingRows).RemoveRange(first, index - first + 1);
            index = first;
        }
        NotifyRegions();
    }
}
