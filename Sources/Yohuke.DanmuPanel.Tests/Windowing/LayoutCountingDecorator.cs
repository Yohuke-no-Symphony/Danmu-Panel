using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Tests.Windowing;

internal sealed class LayoutCountingDecorator : Decorator
{
    internal int MeasurePasses { get; private set; }
    internal int ArrangePasses { get; private set; }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePasses++;
        return base.MeasureOverride(availableSize);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangePasses++;
        return base.ArrangeOverride(finalSize);
    }
}
