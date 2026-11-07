using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>共享设置分组的标题、卡片外观和内容插槽。</summary>
public sealed partial class SettingsCard : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingsCard, string?>(nameof(Title));
    public static readonly StyledProperty<Control?> BodyProperty = AvaloniaProperty.Register<SettingsCard, Control?>(nameof(Body));
    public static readonly DirectProperty<SettingsCard, bool> HasTitleProperty =
        AvaloniaProperty.RegisterDirect<SettingsCard, bool>(nameof(HasTitle), card => card.HasTitle);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    [Content]
    public Control? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public bool HasTitle => !string.IsNullOrEmpty(Title);

    /// <summary>加载布局并保留宿主的数据上下文。</summary>
    public SettingsCard()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            RaisePropertyChanged(HasTitleProperty, !string.IsNullOrEmpty(change.GetOldValue<string?>()), HasTitle);
        }
    }
}
