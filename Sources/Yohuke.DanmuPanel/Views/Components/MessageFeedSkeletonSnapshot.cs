using Avalonia;
using Avalonia.Media;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed record MessageFeedSkeletonSnapshot(Rect Bounds, IReadOnlyList<Rect> Rectangles,
    IImmutableBrush Foreground, double CornerRadius);
