using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>承载 Neo 风格设置窗口的标题、分类和内容区域。</summary>
public sealed partial class SettingsDialogSurface : UserControl
{
    public static readonly StyledProperty<Control?> PageContentProperty =
        AvaloniaProperty.Register<SettingsDialogSurface, Control?>(nameof(PageContent));

    public Control? PageContent
    {
        get => GetValue(PageContentProperty);
        set => SetValue(PageContentProperty, value);
    }

    /// <summary>加载界面模板。</summary>
    public SettingsDialogSurface()
    {
        InitializeComponent();
    }
}
