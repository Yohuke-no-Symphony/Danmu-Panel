using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>将术语行本身作为删除命令参数的本地化菜单。</summary>
public sealed partial class GlossaryRowMenu : ContextMenu
{
    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<GlossaryRowMenu, ICommand?>(nameof(RemoveCommand));

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    /// <summary>加载术语行操作菜单。</summary>
    public GlossaryRowMenu()
    {
        InitializeComponent();
    }
}
