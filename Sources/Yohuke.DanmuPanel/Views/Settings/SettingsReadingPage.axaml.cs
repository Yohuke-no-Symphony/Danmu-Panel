using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>设置窗口的显示、阅读、排序与拼音页面。</summary>
public sealed partial class SettingsReadingPage : UserControl
{
    /// <summary>加载页面布局，业务状态由宿主绑定。</summary>
    public SettingsReadingPage()
    {
        InitializeComponent();
    }
}
