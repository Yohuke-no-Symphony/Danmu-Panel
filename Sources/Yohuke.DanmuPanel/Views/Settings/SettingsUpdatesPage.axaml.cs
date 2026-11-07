using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>设置窗口的版本信息与更新偏好页面。</summary>
public sealed partial class SettingsUpdatesPage : UserControl
{
    /// <summary>加载页面布局，业务状态由宿主绑定。</summary>
    public SettingsUpdatesPage()
    {
        InitializeComponent();
    }
}
