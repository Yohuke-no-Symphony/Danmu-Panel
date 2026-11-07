using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed partial class WorkbenchHeaderTitleSurface : Grid
{
    public static readonly StyledProperty<Control?> TitleContentProperty =
        AvaloniaProperty.Register<WorkbenchHeaderTitleSurface, Control?>(nameof(TitleContent));

    public Control? TitleContent
    {
        get => GetValue(TitleContentProperty);
        set => SetValue(TitleContentProperty, value);
    }

    public WorkbenchHeaderTitleSurface()
    {
        InitializeComponent();
    }
}
