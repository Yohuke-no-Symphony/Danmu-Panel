using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBoxItem : ListBoxItem
{
    internal MessageFeedRow Row { get; }

    internal MessageFeedListBoxItem(MessageFeedRow row)
    {
        Row = row;
        Content = row;
    }

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ListBoxItem);
}
