using Xunit;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Tests;

/// <summary>
/// 验证原生缩放边框、系统按钮和多显示器坐标命中。
/// </summary>
public sealed class WindowsChromeHitTestTests
{
    /// <summary>
    /// 使用带符号屏幕坐标命中八个原生缩放边缘。
    /// </summary>
    [Theory]
    [InlineData(-999, -99, 13)]
    [InlineData(-500, -99, 12)]
    [InlineData(-1, -99, 14)]
    [InlineData(-999, 200, 10)]
    [InlineData(-1, 200, 11)]
    [InlineData(-999, 499, 16)]
    [InlineData(-500, 499, 15)]
    [InlineData(-1, 499, 17)]
    [InlineData(-500, 100, 0)]
    [InlineData(-1001, 100, 0)]
    public void NativeResizeEdgesUseSignedScreenCoordinates(int x, int y, int expected)
    {
        var point = new WindowsPoint { X = x, Y = y };
        var outer = new WindowsRect { Left = -1000, Top = -100, Right = 0, Bottom = 500 };

        var hit = WindowsChromeGeometry.HitResizeFrame(point, outer, new(8, 8, 8, 8), true, false);

        Assert.Equal(expected, (int)hit);
    }

    /// <summary>
    /// 最大化或禁止缩放的窗口不产生缩放命中区域。
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void MaximizedAndNonResizableWindowsHaveNoResizeEdges(bool canResize, bool maximized)
    {
        var point = new WindowsPoint { X = 1, Y = 1 };
        var outer = new WindowsRect { Right = 1000, Bottom = 600 };

        Assert.Equal(WindowsChromeHitTest.NONE,
            WindowsChromeGeometry.HitResizeFrame(point, outer, new(8, 8, 8, 8), canResize, maximized));
    }

    /// <summary>
    /// 窗口消息坐标保留负数显示器偏移。
    /// </summary>
    [Theory]
    [InlineData(-1900, -900)]
    [InlineData(1900, -900)]
    [InlineData(-1900, 900)]
    [InlineData(1900, 900)]
    public void LparamCoordinatesPreserveNegativeMonitorOffsets(int x, int y)
    {
        var packed = (nint)(unchecked((ushort)(short)x) | (unchecked((uint)(ushort)(short)y) << 16));
        var point = WindowsChromeGeometry.GetScreenPoint(packed);

        Assert.Equal(x, point.X);
        Assert.Equal(y, point.Y);
    }

    /// <summary>
    /// 系统按钮命中使用各自真实矩形，按钮间隙不触发操作。
    /// </summary>
    [Theory]
    [InlineData(-288, -11, 8)]
    [InlineData(-239, -11, 9)]
    [InlineData(-141, -11, 20)]
    [InlineData(-320, -11, 0)]
    [InlineData(-160, -11, 0)]
    [InlineData(-100, -11, 0)]
    [InlineData(-141, 20, 0)]
    public void SystemCaptionHitUsesEachActualScreenRectangleIncludingGaps(int x, int y, int expected)
    {
        var titleBar = new WindowsTitleBarInfoEx
        {
            MinimizeBounds = new() { Left = -319, Top = -20, Right = -278, Bottom = 20 },
            MaximizeBounds = new() { Left = -277, Top = -20, Right = -180, Bottom = 20 },
            CloseBounds = new() { Left = -159, Top = -20, Right = -100, Bottom = 20 }
        };

        Assert.Equal(expected, (int)WindowsChromeGeometry.HitCaptionButtons(new() { X = x, Y = y }, in titleBar));
    }

    /// <summary>
    /// 禁用、隐藏或屏幕外的系统按钮不成为自定义点击区域。
    /// </summary>
    [Theory]
    [InlineData(0x00000001U)]
    [InlineData(0x00008000U)]
    [InlineData(0x00010000U)]
    public void NativeUnavailableInvisibleAndOffscreenButtonsDoNotBecomeCustomClickRegions(uint state)
    {
        var titleBar = new WindowsTitleBarInfoEx
        {
            MaximizeBounds = new() { Left = 100, Top = 0, Right = 190, Bottom = 50 },
            MaximizeState = state
        };

        Assert.Equal(WindowsChromeHitTest.NONE,
            WindowsChromeGeometry.HitCaptionButtons(new() { X = 145, Y = 25 }, in titleBar));
    }

    /// <summary>
    /// 最大化右上角的实测关闭区域不会被 DWM 的非按钮命中结果覆盖。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void MaximizedCloseButtonOverridesDwmNonButtonHits(int dwmHit)
    {
        var titleBar = new WindowsTitleBarInfoEx
        {
            CloseBounds = new()
            {
                Left = 1874,
                Top = 0,
                Right = 1920,
                Bottom = 32
            }
        };
        var point = new WindowsPoint
        {
            X = 1919,
            Y = 0
        };

        Assert.Equal(WindowsChromeHitTest.CLOSE,
            WindowsChromeGeometry.HitCaptionButtons(point, in titleBar, (WindowsChromeHitTest)dwmHit));
    }

    /// <summary>
    /// 非按钮命中必须继续进入窗口自身的缩放、拖动与客户区判断。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void DwmNonButtonHitsDoNotBypassManagedFrameHitTesting(int dwmHit)
    {
        var titleBar = new WindowsTitleBarInfoEx();
        var point = new WindowsPoint
        {
            X = 1800,
            Y = 10
        };

        Assert.Equal(WindowsChromeHitTest.NONE,
            WindowsChromeGeometry.HitCaptionButtons(point, in titleBar, (WindowsChromeHitTest)dwmHit));
    }

    /// <summary>
    /// DWM 成功命中的系统按钮保留原生结果。
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(20)]
    public void DwmCaptionButtonHitsRemainAuthoritative(int dwmHit)
    {
        var titleBar = new WindowsTitleBarInfoEx
        {
            CloseBounds = new()
            {
                Left = 1874,
                Top = 0,
                Right = 1920,
                Bottom = 32
            }
        };
        var point = new WindowsPoint
        {
            X = 1919,
            Y = 0
        };

        Assert.Equal((WindowsChromeHitTest)dwmHit,
            WindowsChromeGeometry.HitCaptionButtons(point, in titleBar, (WindowsChromeHitTest)dwmHit));
    }

    /// <summary>
    /// 最大化关闭区域使用原生屏幕坐标，保留四条边界及负坐标显示器语义。
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1874, 0, 20)]
    [InlineData(0, 0, 1919, 31, 20)]
    [InlineData(0, 0, 1873, 0, 0)]
    [InlineData(0, 0, 1920, 0, 0)]
    [InlineData(0, 0, 1919, -1, 0)]
    [InlineData(0, 0, 1919, 32, 0)]
    [InlineData(-1920, -1080, -1, -1080, 20)]
    [InlineData(-1920, -1080, 0, -1080, 0)]
    public void MaximizedCloseButtonUsesActualScreenBounds(int screenLeft, int screenTop, int x, int y, int expected)
    {
        var titleBar = new WindowsTitleBarInfoEx
        {
            CloseBounds = new()
            {
                Left = screenLeft + 1874,
                Top = screenTop,
                Right = screenLeft + 1920,
                Bottom = screenTop + 32
            }
        };
        var point = new WindowsPoint
        {
            X = x,
            Y = y
        };

        Assert.Equal((WindowsChromeHitTest)expected,
            WindowsChromeGeometry.HitCaptionButtons(point, in titleBar, WindowsChromeHitTest.TOP_RIGHT));
    }
}
