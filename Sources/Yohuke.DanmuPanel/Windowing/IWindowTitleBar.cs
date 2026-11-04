using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Windowing;

internal interface IWindowTitleBar
{
    Control View { get; }
    Thickness CaptionInsets { get; set; }
    bool IsDragRegion(Point windowPoint);
}
