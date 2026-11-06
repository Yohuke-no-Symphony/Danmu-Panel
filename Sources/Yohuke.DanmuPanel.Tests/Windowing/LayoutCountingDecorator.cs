using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Tests.Windowing;

internal sealed class LayoutCountingDecorator : Decorator
{
    internal int MeasurePasses { get; private set; }
    internal int InfiniteWidthMeasurePasses { get; private set; }
    internal int ArrangePasses { get; private set; }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePasses++;
        if (double.IsPositiveInfinity(availableSize.Width))
        {
            InfiniteWidthMeasurePasses++;
        }
        return base.MeasureOverride(availableSize);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangePasses++;
        return base.ArrangeOverride(finalSize);
    }
}
