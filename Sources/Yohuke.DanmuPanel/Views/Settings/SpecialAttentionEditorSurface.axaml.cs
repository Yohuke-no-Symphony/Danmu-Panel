using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>特别关注名单、颜色与弹窗查找入口的界面布局。</summary>
public sealed partial class SpecialAttentionEditorSurface : UserControl
{
    public static readonly StyledProperty<ColorInput?> UserNameColorInputProperty =
        AvaloniaProperty.Register<SpecialAttentionEditorSurface, ColorInput?>(nameof(UserNameColorInput));
    public static readonly StyledProperty<ColorInput?> ContentColorInputProperty =
        AvaloniaProperty.Register<SpecialAttentionEditorSurface, ColorInput?>(nameof(ContentColorInput));
    public static readonly StyledProperty<ICommand?> OpenSearchCommandProperty =
        AvaloniaProperty.Register<SpecialAttentionEditorSurface, ICommand?>(nameof(OpenSearchCommand));

    public ColorInput? UserNameColorInput
    {
        get => GetValue(UserNameColorInputProperty);
        set => SetValue(UserNameColorInputProperty, value);
    }

    public ColorInput? ContentColorInput
    {
        get => GetValue(ContentColorInputProperty);
        set => SetValue(ContentColorInputProperty, value);
    }

    public ICommand? OpenSearchCommand
    {
        get => GetValue(OpenSearchCommandProperty);
        set => SetValue(OpenSearchCommandProperty, value);
    }

    /// <summary>加载特别关注名单与颜色设置布局。</summary>
    public SpecialAttentionEditorSurface()
    {
        InitializeComponent();
    }
}
