using Avalonia.Controls;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBox(Func<MessageFeedRow> createRow, MessageKind kind = MessageKind.Danmu) : ListBox
{
    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <inheritdoc />
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = item is MessageLoadingPlaceholderViewModel
            ? typeof(MessageLoadingListBoxItem)
            : typeof(MessageFeedListBoxItem);
        return true;
    }

    /// <inheritdoc/>
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return item is MessageLoadingPlaceholderViewModel
            ? new MessageLoadingListBoxItem(kind)
            : new MessageFeedListBoxItem(createRow());
    }

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is MessageFeedListBoxItem messageContainer)
        {
            messageContainer.Content = messageContainer.Row;
        }
        else if (container is MessageLoadingListBoxItem placeholderContainer)
        {
            placeholderContainer.Content = placeholderContainer.Skeleton;
        }
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is MessageFeedListBoxItem messageContainer)
        {
            messageContainer.Row.ContextMenu?.Close();
        }
        base.ClearContainerForItemOverride(container);
        container.ClearValue(DataContextProperty);
    }
}
