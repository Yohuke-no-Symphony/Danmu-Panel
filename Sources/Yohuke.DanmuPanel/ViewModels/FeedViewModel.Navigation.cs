using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    internal async Task<MessageItemViewModel?> FindMessageAsync(MessageRecord requested,
        CancellationToken cancellationToken)
    {
        var current = room;
        var version = revision;
        if (disposed || current is null || requested.RoomTabId != current.Id || requested.Kind != kind ||
            kind == MessageKind.Danmu)
        {
            return null;
        }

        var record = await runtime.Messages.GetByIdAsync(requested.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || revision != version || room?.Id != current.Id || record is null ||
            record.RoomTabId != current.Id || record.Kind != kind)
        {
            return null;
        }

        var filter = record.IsRead ? MessageReadFilter.Read : MessageReadFilter.Unread;
        var expectedVersion = version + (ReadFilter == filter ? 0 : 1);
        await SelectReadFilterAsync(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed || revision != expectedVersion || room?.Id != current.Id || ReadFilter != filter)
        {
            return null;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = request.Token;
        version = revision;
        await RefreshAsync();
        while (!disposed && revision == version && room?.Id == current.Id && ReadFilter == filter)
        {
            token.ThrowIfCancellationRequested();
            var item = Items.FirstOrDefault(value => value.Record.Id == record.Id);
            if (item is not null)
            {
                return item;
            }

            var previousCount = Items.Count;
            await LoadMoreAsync();
            token.ThrowIfCancellationRequested();
            if (Items.Count <= previousCount)
            {
                return null;
            }
        }
        return null;
    }
}
