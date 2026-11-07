using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>设置窗口的弹幕屏蔽页面。</summary>
public sealed partial class SettingsBlockingPage : UserControl
{
    public static readonly StyledProperty<Control?> EditorProperty =
        AvaloniaProperty.Register<SettingsBlockingPage, Control?>(nameof(Editor));

    public Control? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>加载页面布局，业务状态由宿主绑定。</summary>
    public SettingsBlockingPage()
    {
        InitializeComponent();
    }
}
