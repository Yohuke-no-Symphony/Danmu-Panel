using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Input;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageColumnSplitter : Thumb
{
    internal event EventHandler<PointerPressedEventArgs>? ResizePressed;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Thumb);

    public MessageColumnSplitter()
    {
        AvaloniaXamlLoader.Load(this);
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
