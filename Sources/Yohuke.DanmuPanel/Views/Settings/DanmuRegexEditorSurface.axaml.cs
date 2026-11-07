using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>逐条弹幕正则与独立屏蔽选项的设置布局。</summary>
public sealed partial class DanmuRegexEditorSurface : UserControl
{
    /// <summary>加载正则列表及增删操作的界面布局。</summary>
    public DanmuRegexEditorSurface()
    {
        InitializeComponent();
    }
}
