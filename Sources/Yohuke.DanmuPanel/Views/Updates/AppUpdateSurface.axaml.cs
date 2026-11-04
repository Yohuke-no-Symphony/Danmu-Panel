using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yohuke.DanmuPanel.Views.Updates;

/// <summary>显示更新检查状态、版本信息和发布说明。</summary>
public sealed partial class AppUpdateSurface : UserControl
{
    /// <summary>加载更新检查表面。</summary>
    public AppUpdateSurface()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
