using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageEventSummarySurface : Border
{
    private readonly Border frame;
    private readonly MessageIdentitySurface identity;
    private readonly Grid detail;
    private readonly MessageTemplateHost body;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>创建普通弹幕中的付费事件摘要。</summary>
    public MessageEventSummarySurface()
    {
        AvaloniaXamlLoader.Load(this);
        frame = this.FindControl<Border>("SummaryFrame")!;
        identity = this.FindControl<MessageIdentitySurface>("SummaryIdentity")!;
        detail = this.FindControl<Grid>("SummaryDetail")!;
        body = this.FindControl<MessageTemplateHost>("SummaryBodyHost")!;
        frame.PropertyChanged += OnFramePropertyChanged;
        UpdateContentMargins();
    }

    internal void SetServices(IUserAvatarService? avatars, EmoticonBitmapCache? bitmapCache)
    {
        body.PreparingChild += child => ((MessageBodyPanel)child).SetServices(avatars, bitmapCache);
    }

    private void OnFramePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BorderThicknessProperty)
        {
            UpdateContentMargins();
        }
    }

    private void UpdateContentMargins()
    {
        var thickness = frame.BorderThickness;
        identity.Margin = new(thickness.Left + 8, thickness.Top + 3, 0, thickness.Bottom + 3);
        detail.Margin = new(0, thickness.Top + 3, thickness.Right + 8, thickness.Bottom + 3);
    }
}
