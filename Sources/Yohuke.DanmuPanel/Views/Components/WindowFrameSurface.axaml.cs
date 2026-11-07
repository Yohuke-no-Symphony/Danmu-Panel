using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class WindowFrameSurface : DockPanel
{
    public static readonly StyledProperty<object?> BodyContentProperty =
        AvaloniaProperty.Register<WindowFrameSurface, object?>(nameof(BodyContent));
    public static readonly StyledProperty<Control?> RightContentProperty =
        AvaloniaProperty.Register<WindowFrameSurface, Control?>(nameof(RightContent));

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    public Control? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    internal WindowTitleBar TitleBar => SharedTitleBar;

    public WindowFrameSurface()
    {
        InitializeComponent();
    }
}
