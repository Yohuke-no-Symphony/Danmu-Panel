using Avalonia.Controls;
using Avalonia.Input;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedListBoxItem : ListBoxItem
{
    internal MessageFeedRow Row { get; }
    internal MessageFeedRowHost Host { get; }

    internal MessageFeedListBoxItem(MessageFeedRow row)
    {
        Row = row;
        Host = new(row);
        Content = Host;
    }

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
        {
            base.OnPointerPressed(e);
        }
    }
}
