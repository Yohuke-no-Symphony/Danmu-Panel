using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views;

internal sealed partial class ShellHeaderSurface : Grid
{
    public static readonly StyledProperty<Control?> RoomToolbarProperty =
        AvaloniaProperty.Register<ShellHeaderSurface, Control?>(nameof(RoomToolbar));

    public Control? RoomToolbar
    {
        get => GetValue(RoomToolbarProperty);
        set => SetValue(RoomToolbarProperty, value);
    }

    internal Menu Menu => WindowMenu;

    public ShellHeaderSurface()
    {
        InitializeComponent();
    }
}
