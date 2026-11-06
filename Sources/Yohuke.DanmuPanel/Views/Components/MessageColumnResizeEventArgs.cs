using Avalonia.Interactivity;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageColumnResizeEventArgs(double ratio, bool isCompleted)
    : RoutedEventArgs(MessageFeedColumnLayout.ResizeRequestedEvent)
{
    internal double Ratio { get; } = ratio;
    internal bool IsCompleted { get; } = isCompleted;
}
