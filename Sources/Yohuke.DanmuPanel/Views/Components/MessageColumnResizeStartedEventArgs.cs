using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageColumnResizeStartedEventArgs(IPointer pointer, Point rootPosition, double width, double ratio)
    : RoutedEventArgs(MessageFeedColumnLayout.ResizeStartedEvent)
{
    internal IPointer Pointer { get; } = pointer;
    internal Point RootPosition { get; } = rootPosition;
    internal double Width { get; } = width;
    internal double Ratio { get; } = ratio;
}
