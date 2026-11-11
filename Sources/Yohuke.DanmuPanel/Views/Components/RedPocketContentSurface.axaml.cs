using Avalonia;
using Avalonia.Controls;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class RedPocketContentSurface : UserControl
{
    public static readonly StyledProperty<IUserAvatarService?> AvatarServiceProperty =
        AvaloniaProperty.Register<RedPocketContentSurface, IUserAvatarService?>(nameof(AvatarService));

    public IUserAvatarService? AvatarService
    {
        get => GetValue(AvatarServiceProperty);
        set => SetValue(AvatarServiceProperty, value);
    }

    /// <summary>加载红包内容与中奖名单模板。</summary>
    public RedPocketContentSurface()
    {
        InitializeComponent();
    }
}
