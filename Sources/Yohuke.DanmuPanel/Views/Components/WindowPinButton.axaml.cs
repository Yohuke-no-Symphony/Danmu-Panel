using Avalonia;
using Avalonia.Controls;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class WindowPinButton : Button
{
    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<WindowPinButton, bool>(nameof(IsPinned));
    public static readonly DirectProperty<WindowPinButton, LucideIconKind> IconProperty =
        AvaloniaProperty.RegisterDirect<WindowPinButton, LucideIconKind>(nameof(Icon), control => control.Icon);
    public static readonly DirectProperty<WindowPinButton, string> PinToolTipProperty =
        AvaloniaProperty.RegisterDirect<WindowPinButton, string>(nameof(PinToolTip), control => control.PinToolTip);

    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    public LucideIconKind Icon => IsPinned ? LucideIconKind.PinOff : LucideIconKind.Pin;
    public string PinToolTip => Localization.Get(IsPinned ? "UI.TitleBarView.UnpinWindow" : "UI.TitleBarView.PinWindow");

    protected override Type StyleKeyOverride => typeof(Button);

    public WindowPinButton()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPinnedProperty)
        {
            RaisePropertyChanged(IconProperty, IsPinned ? LucideIconKind.Pin : LucideIconKind.PinOff, Icon);
            RefreshToolTip();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshToolTip();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshToolTip();
    }

    private void RefreshToolTip()
    {
        RaisePropertyChanged(PinToolTipProperty, "", PinToolTip);
    }
}
