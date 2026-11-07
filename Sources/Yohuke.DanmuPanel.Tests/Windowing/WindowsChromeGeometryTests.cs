using System;
using Xunit;
using Avalonia;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Tests;

/// <summary>
/// 验证原生窗口客户区、按钮保留区及多显示器缩放的几何换算。
/// </summary>
public sealed class WindowsChromeGeometryTests
{
    /// <summary>
    /// 显式客户区尺寸只加入可见边框，不重复加入标题高度。
    /// </summary>
    [Theory]
    [InlineData(96U, 337, 210)]
    [InlineData(144U, 497, 310)]
    [InlineData(192U, 657, 411)]
    public void ExplicitClientResizeExcludesNativeCaptionHeight(uint dpi, int width, int height)
    {
        var frame = new WindowsFrameInsets(8, 8, 8, 8);
        var outer = WindowsChromeGeometry.GetOuterSize(new(320.5, 201.25), dpi, frame);

        Assert.Equal(new PixelSize(width, height), outer);
    }

    /// <summary>
    /// 普通窗口扩展顶部客户区，同时保留侧边与底部原生边框。
    /// </summary>
    [Fact]
    public void RestoredClientUsesEntireTopAndPreservesNativeSideBorders()
    {
        var outer = new WindowsRect { Left = -1900, Top = -90, Right = -900, Bottom = 610 };
        var client = WindowsChromeGeometry.GetClientRect(outer, new(8, 8, 8, 8));

        Assert.Equal(-1892, client.Left);
        Assert.Equal(-90, client.Top);
        Assert.Equal(-908, client.Right);
        Assert.Equal(602, client.Bottom);
    }

    /// <summary>
    /// 最大化窗口保留连续的顶部客户区，供系统按钮处理鼠标输入。
    /// </summary>
    [Fact]
    public void MaximizedClientKeepsNativeTopForSystemCaptionButtons()
    {
        var outer = new WindowsRect { Left = -8, Top = -8, Right = 1928, Bottom = 1048 };
        var client = WindowsChromeGeometry.GetClientRect(outer, new(8, 8, 8, 8));

        Assert.Equal(0, client.Left);
        Assert.Equal(-8, client.Top);
        Assert.Equal(1920, client.Right);
        Assert.Equal(1040, client.Bottom);
    }

    /// <summary>
    /// 非整数逻辑尺寸往返转换不会累计标题高度。
    /// </summary>
    [Fact]
    public void FractionalDipRoundTripDoesNotAccumulateCaptionHeight()
    {
        var frame = new WindowsFrameInsets(12, 12, 12, 12);
        var outerSize = WindowsChromeGeometry.GetOuterSize(new(320.5, 201.25), 144, frame);
        var outer = new WindowsRect { Right = outerSize.Width, Bottom = outerSize.Height };
        var client = WindowsChromeGeometry.GetClientRect(outer, frame);
        var restoredSize = new Size((client.Right - client.Left) / 1.5, (client.Bottom - client.Top) / 1.5);

        Assert.Equal(outerSize, WindowsChromeGeometry.GetOuterSize(restoredSize, 144, frame));
    }

    /// <summary>
    /// 分数缩放下恢复实测客户区尺寸不会多出一个像素。
    /// </summary>
    [Theory]
    [InlineData(120U, 83)]
    [InlineData(168U, 9)]
    [InlineData(240U, 166)]
    public void FractionalScalingDoesNotAddAPixelWhenRestoringMeasuredClientSize(uint dpi, int pixels)
    {
        var dipSize = pixels * WindowsChromeGeometry.STANDARD_DPI / dpi;

        Assert.Equal(pixels, WindowsChromeGeometry.ToPixels(dipSize, dpi));
    }

    /// <summary>
    /// 负坐标显示器中的原生按钮边界换算为客户区逻辑单位。
    /// </summary>
    [Fact]
    public void CaptionReservationConvertsWindowCoordinatesToClientDipsOnNegativeScreen()
    {
        var window = new WindowsRect { Left = -1920, Top = -100, Right = 0, Bottom = 980 };
        var origin = new WindowsPoint { X = -1908, Y = -100 };
        var buttons = new WindowsRect { Left = 1722, Top = 2, Right = 1908, Bottom = 45 };

        var insets = WindowsChromeGeometry.GetCaptionInsets(window, origin, 1896, buttons, 144);

        Assert.Equal(new Thickness(0, 0, 124, 0), insets);
    }

    /// <summary>
    /// 标题按钮保留区限制在客户区范围内。
    /// </summary>
    [Fact]
    public void CaptionReservationCannotExtendOutsideClient()
    {
        var window = new WindowsRect { Left = -1920 };
        var origin = new WindowsPoint { X = -1908 };
        var buttons = new WindowsRect { Left = 0, Right = 1908, Bottom = 45 };

        Assert.Equal(1264, WindowsChromeGeometry.GetCaptionInsets(window, origin, 1896, buttons, 144).Right);
        buttons.Left = 2000;
        Assert.Equal(0, WindowsChromeGeometry.GetCaptionInsets(window, origin, 1896, buttons, 144).Right);
    }

    /// <summary>
    /// 原生按钮透明区域使用实测坐标并覆盖顶部边缘。
    /// </summary>
    [Fact]
    public void CaptionApertureUsesMeasuredClientCoordinatesAndIncludesItsTopEdge()
    {
        var window = new WindowsRect { Left = -1920, Top = -100, Right = 0, Bottom = 980 };
        var origin = new WindowsPoint { X = -1908, Y = -100 };
        var buttons = new WindowsRect { Left = 1722, Top = 2, Right = 1908, Bottom = 45 };

        var aperture = WindowsChromeGeometry.GetCaptionAperture(window, origin, new(1264, 720), buttons, 144);

        Assert.Equal(new Rect(1140, 0, 124, 30), aperture);
    }

    /// <summary>
    /// 最大化时透明区域包含屏幕外的顶部客户区，并裁剪到实际客户区宽度。
    /// </summary>
    [Fact]
    public void MaximizedCaptionApertureIncludesOffscreenClientTop()
    {
        var window = new WindowsRect { Left = -8, Top = -8, Right = 1928, Bottom = 1088 };
        var origin = new WindowsPoint
        {
            Y = -8
        };
        var buttons = new WindowsRect { Left = 1782, Top = 0, Right = 1936, Bottom = 40 };

        Assert.Equal(new Rect(1774, 0, 146, 40),
            WindowsChromeGeometry.GetCaptionAperture(window, origin, new(1920, 1088), buttons, 96));
    }

    /// <summary>
    /// 最大化保留系统顶部客户区，同时让标题、按钮透明区域与玻璃范围在不同缩放下对齐。
    /// </summary>
    [Theory]
    [InlineData(96U, 8, 0, 0)]
    [InlineData(120U, 10, 0, 0)]
    [InlineData(144U, 12, 0, 0)]
    [InlineData(192U, 16, 0, 0)]
    [InlineData(96U, 8, -1920, -1080)]
    [InlineData(120U, 10, -2560, -1440)]
    [InlineData(144U, 12, -2560, -1440)]
    [InlineData(192U, 16, -3840, -2160)]
    public void MaximizedCaptionAndContentStayAlignedAcrossDpiAndMonitors(uint dpi, int framePixels,
        int screenLeft, int screenTop)
    {
        var scale = dpi / WindowsChromeGeometry.STANDARD_DPI;
        var width = WindowsChromeGeometry.ToPixels(1000, dpi);
        var height = WindowsChromeGeometry.ToPixels(700, dpi);
        var captionWidth = WindowsChromeGeometry.ToPixels(138, dpi);
        var captionHeight = WindowsChromeGeometry.ToPixels(20, dpi);
        var frame = new WindowsFrameInsets(framePixels, framePixels, framePixels, framePixels);
        var outer = new WindowsRect
        {
            Left = screenLeft - framePixels,
            Top = screenTop - framePixels,
            Right = screenLeft + width + framePixels,
            Bottom = screenTop + height + framePixels
        };
        var client = WindowsChromeGeometry.GetClientRect(outer, frame);
        var origin = new WindowsPoint
        {
            X = client.Left,
            Y = client.Top
        };
        var buttons = new WindowsRect
        {
            Left = framePixels + width - captionWidth,
            Top = framePixels,
            Right = framePixels + width,
            Bottom = framePixels + captionHeight
        };
        var clientSize = new Size((client.Right - client.Left) / scale, (client.Bottom - client.Top) / scale);
        var insets = WindowsChromeGeometry.GetContentInsets(frame, dpi, true);
        var aperture = WindowsChromeGeometry.GetCaptionAperture(outer, origin, clientSize, buttons, dpi);
        var titleHeight = WindowsChromeGeometry.GetTitleBarHeight(20, aperture, insets);

        Assert.Equal(outer.Top, client.Top);
        Assert.Equal(screenTop, client.Top + WindowsChromeGeometry.ToPixels(insets.Top, dpi));
        Assert.Equal(screenLeft, client.Left);
        Assert.Equal(screenLeft + width, client.Right);
        Assert.Equal(screenTop + height, client.Bottom);
        Assert.Equal(new Thickness(0, 8, 0, 0), insets);
        Assert.Equal(width - captionWidth, aperture.Left * scale, 8);
        Assert.Equal(captionWidth, WindowsChromeGeometry.ToPixels(aperture.Width, dpi));
        Assert.Equal(0, aperture.Top);
        Assert.Equal(28, aperture.Height);
        Assert.Equal(20, titleHeight);
        Assert.Equal(42, WindowsChromeGeometry.GetTitleBarHeight(42, aperture, insets));
        Assert.Equal(framePixels + captionHeight,
            WindowsChromeGeometry.GetExtendedFrameTop(titleHeight, insets, dpi));
        var captionInsets = WindowsChromeGeometry.GetCaptionInsets(outer, origin, width, buttons, dpi);
        Assert.Equal(captionWidth, WindowsChromeGeometry.ToPixels(captionInsets.Right, dpi));
    }

    /// <summary>
    /// 普通或全屏窗口不需要最大化边框补偿。
    /// </summary>
    [Theory]
    [InlineData(96U)]
    [InlineData(120U)]
    [InlineData(144U)]
    [InlineData(192U)]
    public void NonMaximizedContentHasNoFrameCompensation(uint dpi)
    {
        Assert.Equal(default, WindowsChromeGeometry.GetContentInsets(new(8, 8, 8, 8), dpi, false));
    }

    /// <summary>
    /// 隐藏或无效的原生按钮边界不可用。
    /// </summary>
    [Fact]
    public void HiddenOrUndefinedCaptionBoundsAreRejected()
    {
        Assert.False(WindowsChromeGeometry.IsUsableCaptionBounds(default));
        Assert.False(WindowsChromeGeometry.IsUsableCaptionBounds(new() { Left = -1, Right = 100, Bottom = 40 }));
        Assert.False(WindowsChromeGeometry.IsUsableCaptionBounds(new() { Left = 100, Right = 100, Bottom = 40 }));
        Assert.True(WindowsChromeGeometry.IsUsableCaptionBounds(new() { Left = 100, Right = 238, Bottom = 40 }));
    }

    /// <summary>
    /// 非法客户区尺寸在调用原生 API 前被拒绝。
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(10, double.NegativeInfinity)]
    [InlineData(0, 10)]
    [InlineData(10, -1)]
    public void InvalidClientSizesAreRejectedBeforeNativeCalls(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsChromeGeometry.GetOuterSize(new(width, height), 96, new(8, 8, 8, 8)));
    }

    /// <summary>
    /// 非法缩放和原生整数溢出不会进入窗口尺寸计算。
    /// </summary>
    [Fact]
    public void InvalidDpiAndNativeIntegerOverflowAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsChromeGeometry.ToPixels(40, 0));
        Assert.Throws<OverflowException>(() => WindowsChromeGeometry.ToPixels(double.MaxValue, 192));
    }
}
