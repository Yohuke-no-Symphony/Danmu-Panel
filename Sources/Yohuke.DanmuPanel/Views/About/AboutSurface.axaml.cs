using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yohuke.DanmuPanel.Views.About;

/// <summary>呈现应用标识、构建信息及项目链接。</summary>
public sealed partial class AboutSurface : UserControl
{
    /// <summary>加载关于页表面。</summary>
    public AboutSurface()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
