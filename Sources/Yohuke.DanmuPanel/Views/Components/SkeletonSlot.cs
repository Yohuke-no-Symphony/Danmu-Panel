using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class SkeletonSlot : AvaloniaObject
{
    public static readonly AttachedProperty<double> WidthRatioProperty =
        AvaloniaProperty.RegisterAttached<SkeletonSlot, Control, double>("WidthRatio");
    public static readonly AttachedProperty<double> HeightRatioProperty =
        AvaloniaProperty.RegisterAttached<SkeletonSlot, Control, double>("HeightRatio", 0.65);
    public static readonly AttachedProperty<HorizontalAlignment> AlignmentProperty =
        AvaloniaProperty.RegisterAttached<SkeletonSlot, Control, HorizontalAlignment>("Alignment", HorizontalAlignment.Left);
    public static readonly AttachedProperty<int> OrderProperty =
        AvaloniaProperty.RegisterAttached<SkeletonSlot, Control, int>("Order");

    public static int GetOrder(Control control)
    {
        return control.GetValue(OrderProperty);
    }

    public static void SetOrder(Control control, int value)
    {
        control.SetValue(OrderProperty, value);
    }

    public static double GetWidthRatio(Control control)
    {
        return control.GetValue(WidthRatioProperty);
    }

    public static void SetWidthRatio(Control control, double value)
    {
        control.SetValue(WidthRatioProperty, value);
    }

    public static double GetHeightRatio(Control control)
    {
        return control.GetValue(HeightRatioProperty);
    }

    public static void SetHeightRatio(Control control, double value)
    {
        control.SetValue(HeightRatioProperty, value);
    }

    public static HorizontalAlignment GetAlignment(Control control)
    {
        return control.GetValue(AlignmentProperty);
    }

    public static void SetAlignment(Control control, HorizontalAlignment value)
    {
        control.SetValue(AlignmentProperty, value);
    }
}
