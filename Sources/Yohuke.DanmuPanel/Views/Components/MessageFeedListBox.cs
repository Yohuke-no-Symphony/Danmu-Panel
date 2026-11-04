using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBox(Func<MessageFeedRow> createRow) : ListBox
{
    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <inheritdoc/>
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return new MessageFeedListBoxItem(createRow());
    }

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        var messageContainer = (MessageFeedListBoxItem)container;
        messageContainer.Content = messageContainer.Row;
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(Control container)
    {
        ((MessageFeedListBoxItem)container).Row.ContextMenu?.Close();
        base.ClearContainerForItemOverride(container);
        container.ClearValue(DataContextProperty);
    }
}
