using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class ShimmerVisualHandler : CompositionCustomVisualHandler
{
    private const double CYCLE_MILLISECONDS = 1800;
    private static IReadOnlyList<ImmutableGradientStop> GradientStops { get; } =
    [
        new(0, Color.FromArgb(100, 0, 0, 0)),
        new(0.25, Color.FromArgb(100, 0, 0, 0)),
        new(0.5, Colors.Black),
        new(0.75, Color.FromArgb(100, 0, 0, 0)),
        new(1, Color.FromArgb(100, 0, 0, 0))
    ];
    private ShimmerTextSnapshot? snapshot;
    private TimeSpan startedAt;
    private bool frameRequested;

    /// <inheritdoc />
    public override void OnMessage(object message)
    {
        var wasRunning = snapshot is not null;
        snapshot?.Dispose();
        snapshot = message as ShimmerTextSnapshot;
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
        using (context.PushOpacityMask(mask, snapshot.Bounds))
        {
            snapshot.Draw(context);
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
