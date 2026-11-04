namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class HoverMarqueeController
{
    private static readonly TimeSpan REPEAT_DELAY = TimeSpan.FromSeconds(1);
    private readonly TimeSpan startDelay;
    private readonly double scrollSpeed;
    private TimeSpan animationDuration;

    internal HoverMarqueeController(TimeSpan startDelay, double scrollSpeed)
    {
        if (startDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startDelay));
        }

        if (!double.IsFinite(scrollSpeed) || scrollSpeed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scrollSpeed));
        }

        this.startDelay = startDelay;
        this.scrollSpeed = scrollSpeed;
    }

    internal double Offset { get; private set; }

    internal double MaxOffset { get; private set; }

    internal bool CanScroll => MaxOffset > 0;

    internal bool IsComplete => CanScroll && Offset >= MaxOffset;

    internal void UpdateViewport(double extentWidth, double viewportWidth)
    {
        if (!double.IsFinite(extentWidth) || !double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            Reset();
            return;
        }

        var maxOffset = Math.Max(0, extentWidth - viewportWidth);
        if (MaxOffset == maxOffset)
        {
            return;
        }

        MaxOffset = maxOffset;
        Offset = Math.Min(Offset, MaxOffset);
        animationDuration = Offset > 0 ? startDelay + TimeSpan.FromSeconds(Offset / scrollSpeed) : TimeSpan.Zero;
    }

    internal double Advance(TimeSpan elapsed, bool repeat = false)
    {
        if (elapsed <= TimeSpan.Zero || !CanScroll || !repeat && IsComplete)
        {
            return Offset;
        }

        animationDuration += elapsed;
        var seconds = animationDuration.TotalSeconds;
        if (repeat)
        {
            var cycleSeconds = startDelay.TotalSeconds + MaxOffset / scrollSpeed + REPEAT_DELAY.TotalSeconds;
            seconds %= cycleSeconds;
        }

        Offset = Math.Clamp((seconds - startDelay.TotalSeconds) * scrollSpeed, 0, MaxOffset);
        return Offset;
    }

    internal void Reset()
    {
        animationDuration = TimeSpan.Zero;
        Offset = 0;
        MaxOffset = 0;
    }
}
