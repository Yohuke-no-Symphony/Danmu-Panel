using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>设置窗口的外观、边框与配色页面。</summary>
public sealed partial class SettingsAppearancePage : UserControl
{
    public static readonly StyledProperty<Control?> ColorEditorProperty =
        AvaloniaProperty.Register<SettingsAppearancePage, Control?>(nameof(ColorEditor));

    public Control? ColorEditor
    {
        get => GetValue(ColorEditorProperty);
        set => SetValue(ColorEditorProperty, value);
    }

    /// <summary>加载页面布局，业务状态由宿主绑定。</summary>
    public SettingsAppearancePage()
    {
        InitializeComponent();
    }
}
