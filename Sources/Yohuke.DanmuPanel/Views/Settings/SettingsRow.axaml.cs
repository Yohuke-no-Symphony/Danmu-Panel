using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>共享设置标签与输入控件的双列布局。</summary>
public sealed partial class SettingsRow : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<SettingsRow, string?>(nameof(Label));
    public static readonly StyledProperty<Control?> EditorProperty = AvaloniaProperty.Register<SettingsRow, Control?>(nameof(Editor));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    [Content]
    public Control? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>加载布局并将内容的数据上下文交给宿主。</summary>
    public SettingsRow()
    {
        InitializeComponent();
    }
}
