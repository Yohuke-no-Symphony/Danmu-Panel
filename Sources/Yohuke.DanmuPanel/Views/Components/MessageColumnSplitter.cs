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
    internal event EventHandler<PointerPressedEventArgs>? ResizePressed;

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

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }
        e.PreventGestureRecognition();
        Focus();
        ResizePressed?.Invoke(this, e);
    }
}
