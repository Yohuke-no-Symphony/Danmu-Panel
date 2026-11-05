using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedSkeletonVisualHandler : CompositionCustomVisualHandler
{
    private const double CYCLE_MILLISECONDS = 1800;
    internal const double BASE_OPACITY = 0.08;
    private const double HIGHLIGHT_OPACITY = 0.12;
    private static IReadOnlyList<ImmutableGradientStop> GradientStops { get; } =
    [
        new(0, Colors.Transparent),
        new(0.35, Colors.Transparent),
        new(0.5, Colors.Black),
        new(0.65, Colors.Transparent),
        new(1, Colors.Transparent)
    ];
    private MessageFeedSkeletonSnapshot? snapshot;
    private TimeSpan startedAt;
    private bool frameRequested;

    /// <inheritdoc />
    public override void OnMessage(object message)
    {
        var wasRunning = snapshot is not null;
        snapshot = message as MessageFeedSkeletonSnapshot;
        if (snapshot is not null)
        {
            if (!wasRunning)
            {
                startedAt = CompositionNow;
            }
            RequestFrame();
        }
        Invalidate();
    }

    /// <inheritdoc />
    public override void OnAnimationFrameUpdate()
    {
        frameRequested = false;
        if (snapshot is not null)
        {
            Invalidate();
            RequestFrame();
        }
    }

    /// <inheritdoc />
    public override void OnRender(ImmediateDrawingContext context)
    {
        if (snapshot is null)
        {
            return;
        }
        var progress = (CompositionNow - startedAt).TotalMilliseconds % CYCLE_MILLISECONDS / CYCLE_MILLISECONDS;
        var start = progress * 2 - 1;
        var mask = new ImmutableLinearGradientBrush(GradientStops,
            startPoint: new(start, 0, RelativeUnit.Relative), endPoint: new(start + 1, 0, RelativeUnit.Relative));
        using (context.PushOpacity(HIGHLIGHT_OPACITY, snapshot.Bounds))
        using (context.PushOpacityMask(mask, snapshot.Bounds))
        {
            foreach (var rectangle in snapshot.Rectangles)
            {
                context.DrawRectangle(snapshot.Foreground, null, rectangle,
                    snapshot.CornerRadius, snapshot.CornerRadius, default);
            }
        }
    }

    private void RequestFrame()
    {
        if (!frameRequested)
        {
            frameRequested = true;
            RegisterForNextAnimationFrameUpdate();
        }
    }
}
