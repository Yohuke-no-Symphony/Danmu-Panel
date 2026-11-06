using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedRowHost : Control
{
    private readonly MessageFeedRow row;

    internal MessageFeedRowHost(MessageFeedRow row)
    {
        this.row = row;
        VisualChildren.Add(row);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        row.Measure(availableSize);
        return row.DesiredSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        row.Arrange(new(finalSize));
        return finalSize;
    }
}
