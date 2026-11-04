using Xunit;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证紧凑文本悬停滚动的延迟、速度和复位。</summary>
public sealed class HoverMarqueeControllerTests
{
    private static readonly TimeSpan START_DELAY = TimeSpan.FromMilliseconds(350);
    private const double SCROLL_SPEED = 60;

    /// <summary>无需滚动的文本保持原位。</summary>
    [Fact]
    public void AdvanceDoesNotMoveWhenContentFitsViewport()
    {
        var controller = new HoverMarqueeController(START_DELAY, SCROLL_SPEED);
        controller.UpdateViewport(100, 100);

        var offset = controller.Advance(TimeSpan.FromSeconds(2));

        Assert.False(controller.CanScroll);
        Assert.Equal(0, offset);
    }

    /// <summary>等待350毫秒后以每秒60像素移动并停在末端。</summary>
    [Fact]
    public void AdvanceWaitsThenMovesAtConfiguredSpeedAndStopsAtEnd()
    {
        var controller = new HoverMarqueeController(START_DELAY, SCROLL_SPEED);
        controller.UpdateViewport(200, 100);

        Assert.Equal(0, controller.Advance(START_DELAY));
        Assert.Equal(60, controller.Advance(TimeSpan.FromSeconds(1)), 3);
        Assert.Equal(100, controller.Advance(TimeSpan.FromSeconds(1)), 3);
        Assert.True(controller.IsComplete);
    }

    /// <summary>可视宽度变化时偏移不超过新的末端。</summary>
    [Fact]
    public void UpdateViewportClampsCurrentOffsetToNewEnd()
    {
        var controller = new HoverMarqueeController(START_DELAY, SCROLL_SPEED);
        controller.UpdateViewport(200, 100);
        controller.Advance(START_DELAY + TimeSpan.FromSeconds(1));

        controller.UpdateViewport(120, 100);

        Assert.Equal(20, controller.Offset, 3);
        Assert.True(controller.IsComplete);
    }

    /// <summary>离开或更换内容后清除进度和旧可视范围。</summary>
    [Fact]
    public void ResetClearsProgressAndViewport()
    {
        var controller = new HoverMarqueeController(START_DELAY, SCROLL_SPEED);
        controller.UpdateViewport(200, 100);
        controller.Advance(START_DELAY + TimeSpan.FromSeconds(1));

        controller.Reset();

        Assert.Equal(0, controller.Offset);
        Assert.Equal(0, controller.MaxOffset);
        Assert.False(controller.CanScroll);
    }

    /// <summary>自动循环在首尾停留，完整显示末尾后从起点开始下一轮。</summary>
    [Fact]
    public void RepeatingAdvanceWaitsAtBothEndsBeforeStartingNextCycle()
    {
        var controller = new HoverMarqueeController(START_DELAY, SCROLL_SPEED);
        controller.UpdateViewport(160, 100);

        Assert.Equal(0, controller.Advance(START_DELAY, repeat: true));
        Assert.Equal(60, controller.Advance(TimeSpan.FromSeconds(1), repeat: true), 3);
        Assert.Equal(60, controller.Advance(TimeSpan.FromMilliseconds(900), repeat: true), 3);
        Assert.Equal(0, controller.Advance(TimeSpan.FromMilliseconds(100), repeat: true), 3);
        Assert.Equal(0, controller.Advance(START_DELAY, repeat: true), 3);
        Assert.Equal(30, controller.Advance(TimeSpan.FromMilliseconds(500), repeat: true), 3);
    }

    /// <summary>单次经过多个循环的时间与逐段推进得到一致位置。</summary>
    [Fact]
    public void RepeatingAdvancePreservesRemainderAcrossMultipleCycles()
    {
        var controller = new HoverMarqueeController(START_DELAY, SCROLL_SPEED);
        controller.UpdateViewport(160, 100);

        Assert.Equal(30, controller.Advance(TimeSpan.FromMilliseconds(5550), repeat: true), 3);
        Assert.Equal(60, controller.Advance(TimeSpan.FromMilliseconds(500), repeat: true), 3);
    }
}
