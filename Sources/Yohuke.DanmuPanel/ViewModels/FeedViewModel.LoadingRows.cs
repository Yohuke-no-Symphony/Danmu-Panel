using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private const int LOADING_HINT_ROW_COUNT = 3;

    private void AddLoadingRows()
    {
        var index = kind == MessageKind.Danmu ? 0 :
            ScrollingRows.TakeWhile(row => row is MessageItemViewModel item && item.Record.IsPriority).Count();
        for (var offset = 0; offset < LOADING_HINT_ROW_COUNT; offset++)
        {
            ScrollingRows.Insert(index + offset, new MessageLoadingPlaceholderViewModel());
        }
        NotifyRegions();
    }

    private void RemoveLoadingRows()
    {
        for (var index = ScrollingRows.Count - 1; index >= 0; index--)
        {
            if (ScrollingRows[index] is MessageLoadingPlaceholderViewModel)
            {
                ScrollingRows.RemoveAt(index);
            }
        }
        NotifyRegions();
    }

    private Task SynchronizeScrollingRowsAsync(CancellationToken cancellationToken)
    {
        return FeedCollectionSynchronizer.SynchronizeAsync(ScrollingRows, ScrollingItems,
            cancellationToken, static row => row is MessageLoadingPlaceholderViewModel);
    }
}
