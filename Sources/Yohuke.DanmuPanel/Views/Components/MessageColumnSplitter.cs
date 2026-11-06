using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageColumnSplitter : Thumb
{
    private IPointer? pointer;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Thumb);

    internal MessageColumnSplitter()
    {
        Classes.Add("messageColumnSplitter");
        Cursor = new(StandardCursorType.SizeWestEast);
        Focusable = true;
        MinWidth = 6;
        Template = new FuncControlTemplate<MessageColumnSplitter>((_, _) =>
        {
            var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
            line.Bind(Border.BackgroundProperty, new TemplateBinding(BackgroundProperty), BindingPriority.Template);
            return new Border { Background = Brushes.Transparent, Child = line };
        });
    }

    internal void ReleasePointer()
    {
        pointer?.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }
        pointer = e.Pointer;
        base.OnPointerPressed(e);
        pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled = true;
        ReleasePointer();
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        pointer = null;
        base.OnPointerCaptureLost(e);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleasePointer();
        base.OnDetachedFromVisualTree(e);
    }
}
