using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageLoadingListBoxItem : ListBoxItem
{
    internal MessageFeedSkeleton Skeleton { get; }

    internal MessageLoadingListBoxItem(MessageKind kind = MessageKind.Danmu)
    {
        Skeleton = new() { Kind = kind };
        IsEnabled = false;
        Focusable = false;
        IsHitTestVisible = false;
        Skeleton.Bind(ForegroundProperty, new DynamicResourceExtension("AppMutedTextBrush"));
        Content = Skeleton;
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ListBoxItem);
}
