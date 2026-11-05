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

    private async Task SynchronizeScrollingRowsAsync(CancellationToken cancellationToken)
    {
        var expected = ScrollingItems.ToHashSet();
        var uiBudget = new FeedUiWorkBudget();
        for (var index = ScrollingRows.Count - 1; index >= 0; index--)
        {
            if (ScrollingRows[index] is MessageItemViewModel item && !expected.Contains(item))
            {
                ScrollingRows.RemoveAt(index);
                await uiBudget.YieldIfNeededAsync(cancellationToken);
            }
        }
        var retained = ScrollingRows.ToHashSet();
        for (var index = 0; index < ScrollingItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ScrollingItems[index];
            if (index < ScrollingRows.Count && ReferenceEquals(ScrollingRows[index], item))
            {
                continue;
            }
            if (retained.Contains(item))
            {
                ScrollingRows.Move(ScrollingRows.IndexOf(item), index);
            }
            else if (index < ScrollingRows.Count && ScrollingRows[index] is MessageLoadingPlaceholderViewModel)
            {
                ScrollingRows[index] = item;
            }
            else
            {
                ScrollingRows.Insert(index, item);
            }
            await uiBudget.YieldIfNeededAsync(cancellationToken);
        }
        while (ScrollingRows.Count > ScrollingItems.Count)
        {
            ScrollingRows.RemoveAt(ScrollingRows.Count - 1);
            await uiBudget.YieldIfNeededAsync(cancellationToken);
        }
    }
}
